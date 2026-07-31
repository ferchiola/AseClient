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
driver y resolver los bugs/gaps encontrados (en progreso), 3) publicar como paquete NuGet propio
(`Chiola.AseClient`), 4) reemplazar la dependencia `AdoNetCore.AseClient` de
`EntityFrameworkCore.Ase` por este paquete propio.

## Stack

.NET 9 (`net9.0` únicamente — ver nota de fork en README sobre por qué se recortó del rango de
targets legacy del original). C# con `LangVersion=latest`. Tests con NUnit 3.x (heredado del
original, no NUnit 4 — ver nota de fork).

## Comandos útiles

```
dotnet build                                                    # toda la solución
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

## Convenciones

- Mismo criterio que `EntityFrameworkCore.Ase`: no asumir comportamiento de ASE por analogía con SQL
  Server u otro motor — verificar contra la instancia real antes de dar por sentado un fix.
- Cambios de comportamiento/bugs reales encontrados y su fix: documentar en `DECISIONS.md` de este
  proyecto (crear cuando haya el primer fix real — todavía no existe, el fork inicial fue un port
  directo a net9.0 sin cambios funcionales).
- No se pidió (todavía) cambiar el framework de tests de NUnit a xUnit, ni resolver los warnings
  `SYSLIB0051`/`SYSLIB0057` (serialización legacy, `X509Certificate2` constructor obsoleto) que
  aparecen al compilar — quedan como candidatos para la fase de análisis, no bloquean nada hoy.

## Deploy

No aplica infraestructura web/IIS/MSSQL — es una librería (paquete NuGet), no un sitio. El "deploy" es
publicar a nuget.org, igual que `EntityFrameworkCore.Ase` (ver ese proyecto para el workflow de GitHub
Actions con Trusted Publishing — todavía no configurado acá, es el paso 3 del plan de arriba).

## Notas

- Repo remoto: todavía no existe en GitHub. `git remote` local tiene `upstream` (fetch-only, apunta a
  `DataAction/AdoNetCore.AseClient`) pero ningún `origin` — falta crear `ferchiola/AseClient` (o el
  nombre que se decida) en GitHub y agregarlo como `origin` antes de poder pushear.
- Historia de git preservada desde el clone del original (no se aplanó) — los commits nuevos de este
  fork quedan encima de esa historia.
