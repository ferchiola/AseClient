# CLAUDE.md

Contexto e instrucciones para Claude Code en este proyecto.

## Descripción

Fork de [DataAction/AdoNetCore.AseClient](https://github.com/DataAction/AdoNetCore.AseClient) (driver
ADO.NET para SAP/Sybase ASE, implementación nativa del protocolo TDS 5.0, Apache-2.0). Ver la nota de
fork al principio de `README.md` para el detalle completo de qué cambió respecto al original.

**Por qué existe este fork**: es la dependencia de driver de
[`Chiola.EntityFrameworkCore.Ase`](../EntityFrameworkCore.Ase/) (`AdoNetCore.AseClient` como paquete
NuGet de terceros, ver `DECISIONS.md` de ese proyecto) — se encontraron varios bugs/gaps reales del
driver mientras se construía el provider de EF Core (GetOrdinal con FieldCount=0, pooling que no
libera conexiones de forma confiable, el charset confiando ciegamente en lo que declara el servidor).
En vez de seguir acumulando workarounds en la capa de EF Core, este fork existe para arreglarlos
directo en la fuente.

**Plan** (a 2026-07-31): 1) fork inicial + atribución en README (hecho), 2) analizar el driver y
resolver los bugs/gaps encontrados (en progreso — `ClearPool`/`ClearPools` y la limpieza proactiva de
idle ya resueltos, ver `DECISIONS.md`), 3) publicar como paquete NuGet propio (`Chiola.AseClient`,
hecho — ver "Deploy" abajo), 4) reemplazar la dependencia `AdoNetCore.AseClient` de
`EntityFrameworkCore.Ase` por este paquete propio (hecho, mismo día — ver
`EntityFrameworkCore.Ase/CLAUDE.md`).

## Stack

`src/AdoNetCore.AseClient` targetea **`net9.0` únicamente**. Hubo un detour el mismo día (2026-07-31):
se restauró/amplió a los 12 targets del original (`netcoreapp1.0`-`2.2`, `net46`, `netstandard2.0`,
`net5.0`-`net9.0`) y después se revirtió a pedido explícito del usuario — el único consumidor real
(`EntityFrameworkCore.Ase`) es net9.0-only, así que no se justificaba mantener/verificar 12 targets
para un paquete con un solo consumidor. Ver `DECISIONS.md` para la historia completa. `LangVersion=latest`
(necesario por un bug real del parser del SDK de esta máquina contra Dapper en el proyecto de tests,
no del código de `src/` en sí — ver `DECISIONS.md`).

**Versión = `9.0.0`**, no sigue el esquema `0.x` del original ni un contador propio — a pedido
explícito del usuario, el número de versión señala directamente el target framework mínimo/único
(`net9.0`), no un nivel de completitud de features ni el historial de releases de upstream.

**Sin proyecto StrongName ni `build/common.props`** — ambos existieron brevemente durante el detour de
arriba y se descartaron: `common.props` ya no tiene sentido con un solo proyecto/target (toda su
config vive directo en el único `.csproj` ahora), y `AdoNetCore.AseClient.StrongName`/su `.snk` no
tienen ningún consumidor real. Ver `DECISIONS.md`.

Proyecto de **tests** (`test/AdoNetCore.AseClient.Tests`): `net9.0`, NUnit 3.x (heredado del original,
no NUnit 4 — ver nota de fork).

## Comandos útiles

```
dotnet build                                                    # toda la solución
dotnet pack src/AdoNetCore.AseClient/AdoNetCore.AseClient.csproj -c Release  # nupkg
dotnet test --filter "TestCategory!=integration&FullyQualifiedName!~Integration"   # solo unit tests (~1200, sin ASE real)
dotnet test --filter "FullyQualifiedName~.Integration."         # suite de integración completa, requiere ASE real
```

### Instancia de ASE para tests de integración

Igual que `EntityFrameworkCore.Ase`: instancia local SAP ASE 16.1 (`DESKTOP`, `C:\SAP\ASE-16_1`),
hostname `SERVER_ASE` resuelto vía `hosts` local a la IP LAN de esta máquina (no `localhost` — ASE
escucha en la IP de red, no en loopback). Si no está arriba: `Start-Process
"C:\SAP\ASE-16_1\install\RUN_DESKTOP.bat"` (puede necesitar una consola elevada si el log
`C:\SAP\ASE-16_1\install\DESKTOP.log` quedó con permisos rotos — ver `EntityFrameworkCore.Ase/DECISIONS.md`
si vuelve a pasar).

`test/AdoNetCore.AseClient.Tests/DatabaseLoginDetails.json` (gitignored, ya creado localmente) apunta
a esa instancia: `{"Server": "SERVER_ASE", "Port": "5000", "Database": "master", "User": "sa", "Pass": "Password"}`.
En otra máquina, recrear ese archivo con los datos reales (ver
`ConnectionStrings.cs`/`https://github.com/DataAction/AdoNetCore.AseClient/wiki/Running-the-integration-tests`
para el formato).

**Si una corrida de tests se cuelga sin motivo aparente** (VSTest la aborta con "Proceso de host de
pruebas bloqueado", o un `CREATE TABLE`/DDL simple no responde nunca): antes de sospechar de un bug de
código, correr `DUMP TRANSACTION master WITH TRUNCATE_ONLY` contra la instancia. `master` (la base
contra la que corren estos tests y los de `EntityFrameworkCore.Ase`) no tiene `trunc log on chkpt`
habilitado — a diferencia de `tempdb` — así que su log de transacciones se llena solo con suficiente
actividad de tests acumulada, y una vez lleno cualquier operación logueada se queda colgada
indefinidamente sin tirar ningún error (`sp_who` la muestra en estado `LOG SUSPEND`). Ver
`EntityFrameworkCore.Ase/DECISIONS.md` (2026-07-31) para el caso real donde esto pasó.

## Convenciones

- Mismo criterio que `EntityFrameworkCore.Ase`: no asumir comportamiento de ASE por analogía con SQL
  Server u otro motor — verificar contra la instancia real antes de dar por sentado un fix.
- Cambios de comportamiento/bugs reales encontrados y su fix: documentar en `DECISIONS.md` de este
  proyecto (ya existe, con los primeros dos fixes reales).
- No se pidió (todavía) cambiar el framework de tests de NUnit a xUnit, ni resolver los warnings
  `SYSLIB0051`/`SYSLIB0057` (serialización legacy, `X509Certificate2` constructor obsoleto) que
  aparecen al compilar — quedan como candidatos para la fase de análisis, no bloquean nada hoy.

## Deploy

No aplica infraestructura web/IIS/MSSQL — es una librería (paquete NuGet), no un sitio. Publicado a
nuget.org (`Chiola.AseClient`) a mano con `dotnet nuget push` usando la API key global de `CLAUDE.md`
raíz — a diferencia de `EntityFrameworkCore.Ase`, todavía **no** tiene el workflow de GitHub Actions
con Trusted Publishing (OIDC) configurado. Para publicar una versión nueva: bump de `<Version>` en
`src/AdoNetCore.AseClient/AdoNetCore.AseClient.csproj`,
`dotnet pack src/AdoNetCore.AseClient/AdoNetCore.AseClient.csproj -c Release`,
`dotnet nuget push ... --api-key <key> --source https://api.nuget.org/v3/index.json`.

## Notas

- Repo remoto: `https://github.com/ferchiola/AseClient` (creado y pusheado 2026-07-31). `git remote`
  local tiene `origin` (push habilitado) y `upstream` (fetch-only, apunta a
  `DataAction/AdoNetCore.AseClient`, push deshabilitado a propósito).
- Historia de git preservada desde el clone del original (no se aplanó) — los commits nuevos de este
  fork quedan encima de esa historia.
- El token de GitHub que usa `git push` acá es un fine-grained PAT cacheado por el credential manager
  de Windows (mismo que usa `EntityFrameworkCore.Ase`) — **scopeado a repos específicos**, no a
  "todos". Si aparece un 403 al pushear un repo nuevo (o "Repository not found" si el repo ni existe
  todavía), hay que crear el repo a mano y agregarlo a la lista de repos del token en GitHub → Settings
  → Developer settings → Personal access tokens. Ese mismo token **no** tiene permiso para crear repos
  nuevos vía API (`POST /user/repos` → 403) ni para hacer unlist/delete de paquetes en nuget.org vía la
  API key global (403 también) — ambas cosas las tiene que hacer el usuario a mano.
- Ícono del paquete (`icon.png`, referenciado desde el `.csproj` de `src/AdoNetCore.AseClient`):
  reemplazado 2026-07-31 — el original seguía siendo literalmente el logo de DataAction (nunca se
  había tocado al hacer el fork). Ahora es un ícono propio simple (cilindro de base de datos, generado
  por Claude vía GDI+/PowerShell, sin relación con ninguna marca) — el mismo se reusó también para
  `Chiola.EntityFrameworkCore.Ase`, que no tenía ninguno.
