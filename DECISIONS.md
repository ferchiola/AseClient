# DECISIONS.md

Registro de decisiones de diseño y bugs reales encontrados/corregidos en este fork, por orden
cronológico. Ver `README.md` para qué cambió respecto a `DataAction/AdoNetCore.AseClient` en el setup
inicial del fork (sin cambios funcionales) — acá arrancan los cambios funcionales de verdad.

## `ClearPool`/`ClearPools` eran no-ops (2026-07-31)

Reportado por el usuario: una DBA notó que la aplicación dejaba conexiones abiertas en el servidor
ASE. Investigado leyendo el driver completo (sin tocar nada) antes de tocar código.

### Diagnóstico

`Internal/ConnectionPoolManager.cs` — `ClearPool(string)`/`ClearPools()` (los métodos internos detrás
de las APIs públicas `AseConnection.ClearPool()`/`AseConnection.ClearPools()`, el equivalente de
`SqlConnection.ClearPool()`/`ClearAllPools()`) eran literalmente:

```csharp
public void ClearPool(string connectionString) { //todo: implement }
public void ClearPools() { //todo: implement }
```

No hacían nada. Sin excepción, sin log — el llamador cree que limpió el pool y no pasó nada. Esto
explica un bug que ya se había pisado sin saber la causa real en `EntityFrameworkCore.Ase`
(`DECISIONS.md`, Fase 5): `AseConnection.ClearPools()` + reintentos de hasta 15×500ms antes de un
`DROP DATABASE` no liberaba la conexión de forma confiable, y se resolvió en su momento con
`Pooling=false` como workaround, atribuyéndolo a una carrera de timing en el pool. No era timing — el
método que se llamaba no hacía nada en absoluto.

Segundo hallazgo relacionado (no arreglado en este cambio, documentado para más adelante): no hay
ningún mecanismo de limpieza proactiva de conexiones idle en el pool (`grep` de `Timer`/`CleanUp`/
`Prune`/`Sweep` sobre todo `src/AdoNetCore.AseClient` — cero resultados). `ConnectionIdleTimeout`/
`ConnectionLifetime` solo se evalúan de forma perezosa, cuando alguien vuelve a pedir una conexión
del pool (`ConnectionPool.FetchIdlePooledConnection`). Si la app tiene un pico de actividad y después
queda inactiva, las conexiones pooleadas quedan abiertas indefinidamente sin que nada las cierre. Este
`ClearPool`/`ClearPools` roto era, hasta ahora, la única palanca que una app tenía para forzar ese
cierre — y no funcionaba.

### Fix: patrón de "generación" del pool

Implementado en `Internal/ConnectionPool.cs` (donde vive la lógica real; `ConnectionPoolManager` solo
delega) — mismo patrón que usa `SqlConnection.ClearPool()` internamente:

- `IInternalConnection` (interfaz) y `InternalConnection` (implementación real) ganaron una propiedad
  nueva `int Generation { get; set; }`. Se estampa en `ConnectionPool.CreateNewPooledConnection`, justo
  después de crear la conexión.
- `ConnectionPool` tiene un campo `_generation` (protegido por el mismo `_mutex` que ya protegía
  `PoolSize`), incrementado por el nuevo método `Clear()`.
- `Clear()`: 1) bumpea `_generation`, 2) vacía `_available` (la cola de conexiones idle) cerrando cada
  una (`RemoveConnection`, que ya hacía `Dispose()` + decrementar `PoolSize`).
- `Release(connection)`: además de la lógica existente (`ShouldRemoveAndReplace` por `IsDoomed`/
  `ConnectionLifetime`/`ConnectionIdleTimeout`), ahora también chequea si
  `connection.Generation != _generation` — si es así, la conexión predata el último `Clear()` (estaba
  en uso *en ese momento*, por eso `Clear()` no la pudo tocar directamente) y se cierra en vez de
  volver al pool.

Con esto: las conexiones idle se cierran al toque cuando se llama `ClearPool`/`ClearPools`; las que
están en uso en ese instante no se tocan (no se puede interrumpir una query en curso), pero quedan
marcadas para cerrarse — no reusarse — la próxima vez que se liberen. Mismo comportamiento que
`SqlConnection.ClearPool()`.

### Qué NO se tocó en este fix

- El segundo hallazgo (falta de limpieza proactiva/timer de conexiones idle) sigue sin resolver —
  queda para una fase posterior si se decide que vale la pena (agregar un timer periódico que llame
  `ShouldRemoveAndReplace` sobre las conexiones en `_available` sin esperar a que alguien las pida).
- No se cambió el comportamiento de `AseConnectionPool`/`AseConnectionPoolManager` (las clases
  públicas que exponen `Size`/`Available`/`NumberOfOpenConnections`) — siguen leyendo el estado real
  del pool interno tal cual, que ahora sí refleja los cierres.

### Tests

- `test/AdoNetCore.AseClient.Tests/Unit/ConnectionPoolTests.cs` (sin ASE real, con los fakes
  `ImmediateConnectionFactory`/`DoNothingInternalConnection` ya existentes en el archivo — se le agregó
  a este último un flag `WasDisposed` para poder verificar el cierre): `Clear_ClosesIdleConnections`,
  `Clear_ConnectionCheckedOutBeforeClear_IsClosedInsteadOfPooled_WhenReleased`,
  `Clear_ConnectionsCreatedAfterClear_AreNotAffectedAndPoolNormally`.
- `test/AdoNetCore.AseClient.Tests/Integration/AseConnectionPoolManagerTests.cs` (real, contra ASE):
  `ClearPool_ClosesIdlePooledConnection`, `ClearPools_ClosesIdlePooledConnectionsAcrossAllPools`,
  `ClearPool_ConnectionInUseWhenCleared_IsClosedInsteadOfPooled_OnceReleased` — estos tres hubieran
  fallado contra el código viejo (`Available` se hubiera quedado en 1 en vez de bajar a 0, porque
  `ClearPool`/`ClearPools` no hacían nada).
- Suite completa verificada en verde después del cambio: 1207 tests unitarios (0 fallos) + los 8 de
  `AseConnectionPoolManagerTests` contra ASE real (0 fallos). La suite de integración completa (~650+
  tests más) no se corrió entera en este cambio — un test host se colgó a mitad de camino en un intento
  anterior por un motivo no relacionado a este fix (a investigar aparte, ver nota abajo).

## Limpieza proactiva de conexiones idle en el pool (2026-07-31)

Segundo hallazgo de la sección anterior, resuelto a pedido explícito del usuario.

### Fix: `Timer` por pool

`ConnectionPool` ahora arranca un `System.Threading.Timer` en su constructor — solo si `Pooling` está
activo y `ConnectionIdleTimeout` o `ConnectionLifetime` están configurados (> 0); si ninguno de los
dos está seteado no hay nada que barrer, mismo criterio que ya usaba el fill inicial a `MinPoolSize`.

- **Intervalo**: `max(2, min(ConnectionIdleTimeout, ConnectionLifetime) / 2)` segundos — atado al
  timeout más chico configurado, para que un `ConnectionIdleTimeout` corto se cumpla con razonable
  prontitud en vez de "eventualmente". Piso de 2s para no generar un loop demasiado ajustado con
  timeouts de 1s (típico en tests).
- **`SweepIdleConnections`**: vacía `_available` con `TryTake` no bloqueante (uno por uno, no hay forma
  de "espiar sin sacar" en un `BlockingCollection`), evalúa `ShouldRemoveAndReplace` (la misma función
  que ya usaba `Release`/`FetchIdlePooledConnection`) sobre cada una, cierra las vencidas
  (reutilizando `RemoveAndReplace`, que además intenta reponer hasta `MinPoolSize` si corresponde) y
  reinserta las sanas. La ventana breve en la que el pool se ve "más vacío" de lo real para un
  `Reserve()` concurrente es benigna — en el peor caso se crea una conexión de más en vez de reusar
  una que está por reinsertarse, se autocorrige al instante.
- **`Timer` de un solo disparo, reprogramado al final de cada corrida** (`_idleSweepTimer.Change(...)`
  en el `finally`) en vez de un `Timer` periódico — evita corridas superpuestas si alguna vez una
  barrida tarda más que el intervalo (pool muy grande).
- Sin `Dispose()` explícito del `Timer` — mismo criterio que el resto del pool: las instancias de
  `ConnectionPool` ya viven para siempre en el diccionario estático de `ConnectionPoolManager` (no hay
  ningún camino de destrucción de pools hoy), así que no se está introduciendo un patrón de vida nuevo.

### Tests

`test/AdoNetCore.AseClient.Tests/Unit/ConnectionPoolTests.cs`:
`IdleSweep_ProactivelyClosesConnectionsPastIdleTimeout_WithoutAnyReserveCall` — reserva y libera una
conexión, y **sin llamar `Reserve()` de nuevo** (el escenario que antes se quedaba colgado para
siempre), espera 5s reales y confirma que `PoolSize`/`Available` bajan a 0 solos.

De paso, se corrigió un bug real en el propio fake `DoNothingInternalConnection` (no en el driver):
`Created`/`LastActive` nunca se seteaban, quedando en `default(DateTime)` — cualquier chequeo de
`ConnectionIdleTimeout`/`ConnectionLifetime` contra esa conexión la daba por vencida
instantáneamente (miles de años de diferencia), sin importar el tiempo real transcurrido. No afectaba
a ningún test existente porque ninguno anterior configuraba esos timeouts > 0 contra este fake, pero
hacía imposible escribir el test de arriba. Fix: `Created`/`LastActive` ahora se inicializan a
`DateTime.UtcNow` en la construcción del fake.

Suite completa verificada en verde después del cambio: 1208 tests unitarios (0 fallos, +1 sobre el
fix anterior) + los 8 de `AseConnectionPoolManagerTests` contra ASE real (0 fallos, sin regresión).

### Nota aparte, no de este fix: un test host se cuelga corriendo la suite de integración completa

Al intentar correr `dotnet test --filter "FullyQualifiedName~.Integration."` completo (antes de este
fix, como baseline), VSTest abortó la corrida con "Proceso de host de pruebas bloqueado" después de
647 tests pasados (0 fallos) — algún test más adelante en el orden de ejecución cuelga el proceso en
vez de fallar limpio. No identificado todavía cuál ni por qué — candidato para la fase de análisis.
