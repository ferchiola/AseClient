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

## Restaurada la matriz completa de target frameworks (2026-07-31)

A pedido explícito del usuario: "esto beneficiaría a los usuarios del driver original, deberíamos
volver a aceptar todos los targets por si le sirve a alguien más". El fork había arrancado (ver
primera sección de este documento y `README.md`) recortado a `net9.0` únicamente, razonado en su
momento como "el único consumidor real es `EntityFrameworkCore.Ase`, que es net9.0". Revertido: un
paquete NuGet multi-target puede servirle a cualquiera que use `AdoNetCore.AseClient` original en un
proyecto viejo, no solo a nuestro propio caso de uso — no hay motivo real para no ofrecer ambas cosas
a la vez.

Se aclaró primero el alcance con el usuario antes de tocar nada, porque había una ambigüedad real: (a)
¿ampliar nuestro propio fork, o (b) preparar los fixes de `ClearPool`/idle-sweep como Pull Request
contra el repo original de DataAction, para que lleguen a todos los usuarios de ESE paquete, no solo a
quien encuentre este fork? El usuario eligió explícitamente (a) — solo ampliar este fork, sin abrir PR
upstream. Si más adelante se decide ir por (b), es un trabajo aparte (un PR necesitaría un diff mínimo
contra la estructura original, no partir de este fork ya reorganizado).

### Qué se restauró

- `build/common.props`: recuperado de `upstream/master` vía `git show`, con la `TargetFrameworks`
  original (`netcoreapp1.0;netcoreapp1.1;netcoreapp2.0;netcoreapp2.1;netcoreapp2.2;net46;netstandard2.0`)
  — se **agregó** `net9.0` a esa lista, no se reemplazó. Metadata de paquete actualizada (Authors,
  RepositoryUrl, etc.) para reflejar este fork en vez de copiar la del original tal cual (sería
  atribución incorrecta). `VersionPrefix` subido a `0.20.0` (de `0.19.2`) — señala que hay cambios de
  comportamiento reales encima de la base del original, no solo un recompile.
- Las condiciones de `DefineConstants` por target se mantuvieron tal cual el original para los targets
  legacy; se agregó `net9.0` a las mismas condiciones donde corresponde, reproduciendo exactamente la
  combinación "más capaz" que se había elegido a mano cuando el fork era net9.0-only (ver la primera
  sección de este documento) — `ENABLE_ARRAY_POOL`, `ENABLE_DB_PROVIDERFACTORY`,
  `ENABLE_SYSTEM_DATA_COMMON_EXTENSIONS`, `ENABLE_CLONEABLE_INTERFACE`, `ENABLE_SYSTEMEXCEPTION`
  definidos para `net9.0`; `ENABLE_DB_DATAPERMISSION` no (sigue sin agregarse el paquete
  `System.Security.Permissions` solo para un stub sin funcionalidad real — mismo criterio que antes).
  `LangVersion=latest` se mantiene, pero ahora **solo para `net9.0`** vía condición (el resto de los
  targets vuelve al `LangVersion=7` original del csproj de src) — el bug de parser de Roslyn contra
  Dapper que forzó ese cambio (ver primera sección) es específico del SDK de .NET 10 de esta máquina
  compilando `net9.0`, no algo que afecte a los targets legacy.
- `src/AdoNetCore.AseClient.StrongName` (proyecto + `build/AdoNetCore.AseClient.snk`, restaurado desde
  `upstream/master` vía `git show` — verificado byte a byte con `git hash-object` que el `.snk`
  binario no se corrompió en la restauración) — vuelve a existir, con `PackageId` propio
  `Chiola.AseClient.StrongName`. `AdoNetCore.AseClient.Benchmark` **no** se restauró (segunda vez que
  se descarta explícitamente: `BenchmarkDotNet` 0.10.14 desactualizado, sin relación con el objetivo de
  este fork) — el pedido del usuario fue específicamente sobre targets, no sobre el proyecto de
  benchmarks.
- Se agregó `PackageReadmeFile`/`README.md` empaquetado (`common.props`) — no era parte de lo pedido,
  pero `dotnet pack` avisaba de su ausencia como best-practice faltante y ya que se estaba tocando la
  metadata del paquete, se sumó.

### Qué NO se restauró

- El proyecto de **tests** sigue targeteando solo `net9.0` (no se le devolvió su propia matriz vieja
  de `TargetFrameworks` ni los paquetes/condiciones específicas de cada target legacy que tenía). Los
  paquetes de test en versiones modernas (NUnit 3.14, Moq 4.20, Dapper 2.x, etc.) no necesariamente
  compilan contra frameworks tan viejos como `netcoreapp1.0`/`net46`, y no hace falta para el objetivo
  real (que el paquete `src/` compile y funcione en cada target) — alcanza con verificar cada target
  de `src/` por separado con `dotnet build -f <tfm>`, que es justamente cómo se verificó este cambio.
- No se auditó línea por línea cada rama `#if` de los targets legacy — se restauraron las condiciones
  tal cual estaban en el `common.props` original (que ya venían probadas por años de uso real del
  proyecto original), solo agregando `net9.0` donde correspondía. Sigue siendo cierto lo que ya decía
  la primera sección de este documento: no hay tests automatizados corriendo contra
  `netcoreapp1.0`/`net46`/etc., solo verificación de que compilan.

### Verificación

- `dotnet build -f <tfm>` para cada uno de los 8 targets de `src/AdoNetCore.AseClient` — los 8
  compilan limpio, 0 errores (`netcoreapp1.0`, `netcoreapp1.1`, `netcoreapp2.0`, `netcoreapp2.1`,
  `netcoreapp2.2`, `net46`, `netstandard2.0`, `net9.0`). Mismo resultado para
  `AdoNetCore.AseClient.StrongName`.
- `dotnet pack -c Release`: genera `Chiola.AseClient.0.20.0.nupkg` con un `lib/<tfm>/` por cada uno de
  los 8 targets — confirmado inspeccionando el contenido del `.nupkg` directamente (no solo que el
  build no tirara error).
- Advertencias `NU1903`/`NU1902` (vulnerabilidades conocidas en `Microsoft.NETCore.App` para
  `netcoreapp2.2`) aparecen al compilar/empaquetar ese target — son inherentes a targetear un
  framework fuera de soporte hace años (la referencia implícita al framework trae esa versión del
  metapaquete), no algo introducido por este cambio ni arreglable sin dejar de targetear esos
  frameworks.
- Suite completa de tests (net9.0, sin cambios de alcance): 1208 unitarios + 8 de
  `AseConnectionPoolManagerTests` contra ASE real, ambos en verde — sin regresión respecto a los dos
  fixes anteriores.

## Agregados `net5.0`–`net8.0` (2026-07-31)

Pedido de seguimiento inmediato al anterior: "podés agregar los net anteriores al 9 que faltan?" — la
matriz restaurada arriba saltaba directo de `netstandard2.0`/`net46` (2019) a `net9.0` (2024), dejando
afuera las versiones intermedias (`net5.0` 2020, `net6.0` 2021 LTS, `net7.0` 2022, `net8.0` 2023 LTS).

`build/common.props`: los 4 targets nuevos se agregaron a `TargetFrameworks` y a las mismas tres
condiciones de `DefineConstants` donde ya estaba `net9.0` (`ENABLE_DB_PROVIDERFACTORY`,
`ENABLE_SYSTEM_DATA_COMMON_EXTENSIONS`/`ENABLE_CLONEABLE_INTERFACE`/`ENABLE_SYSTEMEXCEPTION`,
`ENABLE_ARRAY_POOL`) — misma combinación "más capaz" que ya se justificó para `net9.0`, sin motivo
para que difiera en una versión de .NET más vieja pero igual de moderna en términos de superficie de
API. **No** se agregaron a la condición de `LangVersion=latest` — ese override existía por un bug
puntual del parser de Roslyn en esta máquina compilando `net9.0` contra una llamada genérica de
Dapper (ver primera sección de este documento), específico de esa combinación exacta; verificado que
`net5.0`/`6.0`/`7.0`/`8.0` compilan limpio con su `LangVersion` default (no hizo falta el override).

Ninguna instalación adicional hizo falta: el SDK de .NET 10 ya instalado en esta máquina puede
compilar cualquier target `net5.0` en adelante sin paquetes de referencia extra (a diferencia de los
`netcoreapp1.x`-`2.x`/`net46` restaurados en la sección anterior, que si necesitaron el SDK 2.2.207 y
los reference assemblies de .NET Framework ya instalados).

### Verificación

- `dotnet build -f <tfm>` para los 12 targets de `src/AdoNetCore.AseClient` (los 8 de antes +
  `net5.0`/`net6.0`/`net7.0`/`net8.0`) — los 12 compilan limpio, 0 errores. Mismo resultado para
  `AdoNetCore.AseClient.StrongName`.
- `dotnet pack -c Release`: el `.nupkg` ahora trae 12 carpetas `lib/<tfm>/`, confirmado inspeccionando
  el contenido del archivo directamente.
- Suite completa de tests (net9.0, sin cambios de alcance): 1208 unitarios + 8 de
  `AseConnectionPoolManagerTests` contra ASE real, ambos en verde — sin regresión.

### Nota aparte, no de este fix: un test host se cuelga corriendo la suite de integración completa

Al intentar correr `dotnet test --filter "FullyQualifiedName~.Integration."` completo (antes de este
fix, como baseline), VSTest abortó la corrida con "Proceso de host de pruebas bloqueado" después de
647 tests pasados (0 fallos) — algún test más adelante en el orden de ejecución cuelga el proceso en
vez de fallar limpio. No identificado todavía cuál ni por qué — candidato para la fase de análisis.

## Publicación en NuGet, GitHub, e ícono propio (2026-07-31)

Publicado `Chiola.AseClient` 0.20.0 en nuget.org (a mano, `dotnet nuget push` con la API key global de
`CLAUDE.md` raíz — sin workflow de Trusted Publishing todavía, a diferencia de `EntityFrameworkCore.Ase`).
Repo creado en `github.com/ferchiola/AseClient` y pusheado (historia completa desde el clone del
original preservada). El token de GitHub cacheado en esta máquina resultó ser un fine-grained PAT sin
permiso para crear repos nuevos vía API (`403`) ni para pushear a un repo recién creado hasta que el
usuario lo agregó a mano a la lista de repos permitidos del token — ambos pasos los hizo el usuario.

**Ícono del paquete reemplazado**: `icon.png` seguía siendo literalmente el logo de DataAction (círculo
navy con líneas de colores) — nunca se había tocado al hacer el fork inicial, y no correspondía seguir
usando la marca de otro proyecto para este. Reemplazado por un ícono propio simple (cilindro de base de
datos, navy/ámbar) generado con `System.Drawing`/GDI+ vía PowerShell — sin dependencias externas de
diseño. Requirió bump de versión (`0.20.0` → `0.20.1`, NuGet no permite republicar la misma versión) y
republicar.

### `Chiola.AseClient.StrongName` publicado y descartado en el mismo día

Al restaurar la matriz de targets (sección anterior) también se había restaurado
`AdoNetCore.AseClient.StrongName` (proyecto + `.snk` del original) y publicado como
`Chiola.AseClient.StrongName` (0.20.0 y luego 0.20.1, junto con el cambio de ícono). A pedido explícito
del usuario, se descartó: este fork no tiene ningún consumidor que necesite un ensamblado con strong
name (ni `EntityFrameworkCore.Ase` ni nada más a la vista), y mantener/publicar un segundo paquete
NuGet solo para que quede sin usar no se justificaba.

- Proyecto `src/AdoNetCore.AseClient.StrongName` eliminado del repo y de la solución (`dotnet sln
  remove`). `build/AdoNetCore.AseClient.snk` borrado también (sin ningún otro uso una vez sacado el
  proyecto).
- Las dos versiones ya publicadas en nuget.org (`0.20.0`, `0.20.1`) **no se pudieron unlist por API** —
  la API key global de `CLAUDE.md` raíz solo tiene permiso de push, no de unlist/delete (`403 Forbidden`
  al intentar `dotnet nuget delete`). El usuario lo hizo a mano desde la web de nuget.org (Manage
  Package). No se puede hacer un delete real en nuget.org, solo unlist (el paquete deja de ser
  descubrible/instalable como nuevo, pero sigue existiendo para quien ya lo referencia).
- Verificado que `dotnet build` de la solución completa sigue compilando limpio (0 errores) después de
  sacar el proyecto.

## Revertido a `net9.0` únicamente, versión `9.0.0` (2026-07-31)

Cierre del detour de multi-targeting del mismo día: a pedido explícito del usuario, se volvió a
recortar `src/AdoNetCore.AseClient` a **`net9.0` solo** (revierte las dos secciones anteriores sobre
restaurar/ampliar la matriz de targets) y se le puso versión **`9.0.0`** — a propósito, para que el
número de versión indique directamente el target framework mínimo/único en vez de seguir el esquema
`0.x` del original o un contador propio de features. Mismo pedido incluyó cambiar la dependencia de
`EntityFrameworkCore.Ase` de `AdoNetCore.AseClient` (upstream) a `Chiola.AseClient` (este fork) — ver
`EntityFrameworkCore.Ase/DECISIONS.md` para esa mitad del cambio.

### Qué se sacó

- `build/common.props` eliminado por completo (y la carpeta `build/`, que quedó vacía). Con un solo
  proyecto y un solo target ya no aportaba nada — toda su configuración se volcó directo al único
  `.csproj` de `src/AdoNetCore.AseClient`, incluidos los `DefineConstants` (la misma combinación "más
  capaz" que ya se usaba para `net9.0`: `ENABLE_ARRAY_POOL`, `ENABLE_DB_PROVIDERFACTORY`,
  `ENABLE_SYSTEM_DATA_COMMON_EXTENSIONS`, `ENABLE_CLONEABLE_INTERFACE`, `ENABLE_SYSTEMEXCEPTION` — sin
  `ENABLE_DB_DATAPERMISSION`, mismo criterio de siempre).
- `TargetFramework` (singular) en vez de `TargetFrameworks` (plural) — ya no hace falta la
  infraestructura de multi-targeting de MSBuild para un solo target.
- `AssemblyVersion`/`FileVersion`/`VersionPrefix` (separados, heredados del original) reemplazados por
  un único `<Version>9.0.0</Version>` — se dejó que el SDK derive automáticamente `AssemblyVersion`/
  `FileVersion` a partir de `Version`, en vez de mantenerlos por separado como hacía el original
  (que tenía `AssemblyVersion` pisado en `0.11.0.0`, un artefacto viejo sin relación con el
  `VersionPrefix` real).

### Verificación

- `dotnet build`: solución completa compila limpio, 0 errores.
- Suite completa de tests (sin cambios de alcance): 1208 unitarios + 8 de
  `AseConnectionPoolManagerTests` contra ASE real, ambos en verde — sin regresión.
- `dotnet pack -c Release`: genera `Chiola.AseClient.9.0.0.nupkg` con un solo `lib/net9.0/`,
  confirmado inspeccionando el contenido del archivo.
- Publicado en nuget.org (`dotnet nuget push`, API key global). Las versiones `0.20.0`/`0.20.1`
  quedan publicadas tal cual (no se unlistearon) — el salto de `0.20.1` a `9.0.0` es intencional, no
  un error de versión.
