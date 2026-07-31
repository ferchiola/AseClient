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

**Plan** (a 2026-07-31, en progreso): 1) fork inicial + atribución en README (hecho), 2) analizar el
driver y resolver los bugs/gaps encontrados (en progreso — `ClearPool`/`ClearPools` y la limpieza
proactiva de idle ya resueltos, ver `DECISIONS.md`), 3) publicar como paquete NuGet propio
(`Chiola.AseClient`, hecho — ver "Deploy" abajo), 4) reemplazar la dependencia `AdoNetCore.AseClient`
de `EntityFrameworkCore.Ase` por este paquete propio (pendiente).

## Stack

`src/AdoNetCore.AseClient` targetea **toda la matriz del original** (`netcoreapp1.0`/`1.1`/`2.0`/`2.1`/
`2.2`, `net46`, `netstandard2.0`) **más `net5.0` a `net9.0`** (las 5 versiones "modernas", agregadas en
dos pasadas — primero `net9.0` solo, después el resto a pedido del usuario para no dejar un salto
entre 2019 y 2024) — 12 targets en total. El fork había arrancado recortado a solo `net9.0`, pero se
restauró/amplió la matriz para que el paquete le sirva a cualquiera que use el driver original en un
proyecto más viejo o en cualquier .NET moderno, no solo al caso de uso propio. Ninguno de los targets
nuevos reemplazó algo de la lista original, todos se agregaron encima — ver `build/common.props` para
el detalle de qué `DefineConstants` aplica cada target (`net5.0`-`net9.0` comparten la misma
combinación "más capaz" que se eligió para `net9.0` cuando el fork era net9.0-only). C#
`LangVersion=7` para todos los targets salvo `net9.0` (`latest` — necesario por un bug real del parser
del SDK de esta máquina contra Dapper, no del código, ver `DECISIONS.md`/README; no se detectó el
mismo problema en `net5.0`/`6.0`/`7.0`/`8.0`, que se dejaron en su `LangVersion` default).

**No hay proyecto StrongName** — existió brevemente (`AdoNetCore.AseClient.StrongName`, restaurado del
original, publicado como `Chiola.AseClient.StrongName` 0.20.0/0.20.1) y se descartó a pedido explícito
del usuario (2026-07-31): este fork no necesita un ensamblado con strong name, y mantener/publicar un
segundo paquete que nadie iba a usar no valía la pena. Las dos versiones publicadas quedaron unlisted
en nuget.org (con la API key global de `CLAUDE.md` raíz no se pudo hacer el unlist por API — 403,
la key solo tiene permiso de push — lo hizo el usuario a mano desde la web). El `.snk`
(`build/AdoNetCore.AseClient.snk`) se borró también, sin uso una vez removido el proyecto.

El proyecto de **tests** (`test/AdoNetCore.AseClient.Tests`) sigue targeteando **solo `net9.0`** — no
se restauró su propia matriz vieja (paquetes de test tipo NUnit/Moq/Dapper en versiones modernas no
necesariamente compilan contra `netcoreapp1.0`/`net46`, y no es necesario para verificar que el
paquete multi-target compila: alcanza con que `src/` compile en cada target, verificado con
`dotnet build -f <tfm>` por separado). Tests con NUnit 3.x (heredado del original, no NUnit 4 — ver
nota de fork).

## Comandos útiles

```
dotnet build                                                    # toda la solución (src multi-target + test net9.0)
dotnet build src/AdoNetCore.AseClient/AdoNetCore.AseClient.csproj -f <tfm>   # compilar un target puntual (ver lista arriba)
dotnet pack src/AdoNetCore.AseClient/AdoNetCore.AseClient.csproj -c Release  # nupkg multi-target, con un lib/<tfm> por cada uno
dotnet test --filter "TestCategory!=integration&FullyQualifiedName!~Integration"   # solo unit tests (~1200, sin ASE real, net9.0)
dotnet test --filter "FullyQualifiedName~.Integration."         # suite de integración completa, requiere ASE real (net9.0)
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
con Trusted Publishing (OIDC) configurado. Para publicar una versión nueva: bump de `VersionPrefix` en
`build/common.props`, `dotnet pack src/AdoNetCore.AseClient/AdoNetCore.AseClient.csproj -c Release`,
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
- Ícono del paquete (`icon.png`, referenciado desde `build/common.props`): reemplazado 2026-07-31 — el
  original seguía siendo literalmente el logo de DataAction (nunca se había tocado al hacer el fork).
  Ahora es un ícono propio simple (cilindro de base de datos, generado por Claude vía GDI+/PowerShell,
  sin relación con ninguna marca).
