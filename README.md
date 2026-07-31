# Chiola.AseClient

> **This is a fork of [DataAction/AdoNetCore.AseClient](https://github.com/DataAction/AdoNetCore.AseClient)**,
> a .NET data provider for SAP/Sybase ASE originally created and maintained by
> [DataAction](https://github.com/DataAction) and its contributors. All credit for the original TDS
> protocol implementation goes to them — see [LICENSE](LICENSE) (Apache-2.0, preserved from upstream).
>
> **Why this fork exists:** built as the driver dependency for
> [`Chiola.EntityFrameworkCore.Ase`](https://github.com/ferchiola/EntityFrameworkCore.Ase), a from-scratch
> EF Core provider for SAP ASE. Several real bugs and gaps in the upstream driver were found and worked
> around while building that provider (see its `DECISIONS.md`) — this fork exists to fix them directly
> at the source instead of accumulating workarounds downstream.
>
> **What changed from upstream so far** (as of the initial fork — see the repo's commit history for
> anything after this):
> - Trimmed to target **`net9.0` only**. Upstream targets a wide range of legacy frameworks
>   (`netcoreapp1.0` through `netstandard2.0`/`net46`, from 2016-2018) — this fork has a single modern
>   consumer, so that whole compatibility matrix (and its `#if`/`DefineConstants` complexity) was
>   dropped in favor of one target.
> - Dropped the `AdoNetCore.AseClient.StrongName` project and the `AdoNetCore.AseClient.Benchmark`
>   project (outdated `BenchmarkDotNet` 0.10.14, not relevant to this fork's goal). Neither is needed by
>   the EF Core provider this fork serves.
> - Test project pinned to NUnit 3.x (upstream's actual test suite, ~1200 unit tests + a real
>   integration suite against live ASE) rather than upgrading to NUnit 4 outright, to avoid churn from
>   its `CollectionAssert`/`StringAssert` namespace move — revisit later if worth it.
> - No functional/bug fixes yet — this initial fork is a straight port to net9.0, nothing more. Bug
>   fixes come next (see the project's own notes for what's tracked).
>
> Package published as **`Chiola.AseClient`** on NuGet (not `AdoNetCore.AseClient`, to avoid clashing
> with the upstream package).

A .NET data provider for SAP ASE — fork of DataAction/AdoNetCore.AseClient.

SAP (formerly Sybase) has supported accessing the ASE database management system from ADO.NET for many years. Unfortunately SAP has not yet made a driver available to support .NET Core, so this project enables product teams that are dependent upon ASE to keep moving their application stack forwards.

The current .NET 4 version of SAP's `Sybase.Data.AseClient` driver is a .NET Framework managed wrapper around SAP's unmanged [ADO DB provider](https://en.wikipedia.org/wiki/ActiveX_Data_Objects) and is dependent upon [COM](https://en.wikipedia.org/wiki/Component_Object_Model). COM is a Windows-only technology and will never be available to .NET Core, making it difficult to port the existing SAP driver.

Under the hood, ASE (and Microsoft Sql Server for that matter) relies on an application-layer protocol called [Tabular Data Stream](https://en.wikipedia.org/wiki/Tabular_Data_Stream) to transfer data between the database server and the client application. ASE uses TDS 5.0.

This project provides a .NET Core native implementation of the TDS 5.0 protocol via an ADO.NET DB Provider, making SAP ASE accessible from .NET Core applications hosted on Windows, Linux, Docker and also serverless platforms like [AWS Lambda](https://aws.amazon.com/lambda/).

## Table of Contents
* [Downloads](#downloads)
* [Objectives](#objectives)
* [Performance benchmarks](#performance-benchmarks)
* [Connection strings](#connection-strings)
* [Supported types](#supported-types)
* [Code samples](#code-samples)

## Downloads
The latest stable release of the AdoNetCore.AseClient is [available on NuGet](https://www.nuget.org/packages/AdoNetCore.AseClient).

## Objectives
* Functional parity with the `Sybase.Data.AseClient` provided by SAP. Ideally, our driver will be a drop in replacement for the `Sybase.Data.AseClient` (with some namespace changes). The following types are supported:
    * AseClientFactory - .NET Core 2.1+
    * AseCommand
    * AseCommandBuilder
    * AseConnection
    * AseConnectionPool
    * AseConnectionPoolManager
    * AseDataAdapter
    * AseDataReader
    * AseDbType
    * AseDecimal
    * AseError
    * AseErrorCollection
    * AseException
    * AseInfoMessageEventArgs
    * AseInfoMessageEventHandler
    * AseParameter
    * AseParameterCollection
    * AseRowUpdatedEventArgs - .NET Core 2.0+
    * AseRowUpdatedEventHandler - .NET Core 2.0+
    * AseRowUpdatingEventArgs - .NET Core 2.0+
    * AseRowUpdatingEventHandler - .NET Core 2.0+
    * TraceEnterEventHandler
    * TraceExitEventHandler

* Not all features are currently supported, and some features will not be supported. Refer to upstream's [Unsupported features](https://github.com/DataAction/AdoNetCore.AseClient/wiki/Unsupported-features) wiki page (still applicable — this fork hasn't diverged on feature support yet).
* Performance equivalent to or better than that of `Sybase.Data.AseClient` provided by SAP. This is possible as we are eliminating the COM and OLE DB layers from this driver and .NET Core is fast.
* Target `net9.0` (see the fork notice at the top of this README for why this differs from upstream's wider target matrix).
* Should work with [Dapper](https://github.com/StackExchange/Dapper) at least as well as the `Sybase.Data.AseClient`

## Performance benchmarks

The benchmark project (`AdoNetCore.AseClient.Benchmark`) and its historical results against `Sybase.Data.AseClient` were dropped in this fork (see the fork notice above) — not relevant to this fork's goal of serving `Chiola.EntityFrameworkCore.Ase`. See [upstream's README](https://github.com/DataAction/AdoNetCore.AseClient#performance-benchmarks) for the original methodology and results.

## Connection strings
[connectionstrings.com](https://www.connectionstrings.com/sybase-adaptive/) lists the following connection string properties for the ASE ADO.NET Data Provider. In keeping with our objective of being a drop-in replacement for the `Sybase.Data.AseClient`, we aim to use identical connection string syntax to the `Sybase.Data.AseClient`, however our support for the various properties will be limited. Our support is as follows:

| Property                                                                                   | Support   | Notes
| ------------------------------------------------------------------------------------------ |:---------:| -----
| `AnsiNull`                                                                                 | &#10003; | By default (0) AnsiNull is disabled which means that SQL statements can use `= NULL` and `IS NULL` syntax. Set to 1 to instruct the connection to only permit `IS NULL` syntax.
| `ApplicationName` or `Application Name`                                                    | &#10003;
| `BufferCacheSize`                                                                          | &#10003; | Buffer caching is automatically managed via an internal [ArrayPool<T>](https://docs.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1.create?view=netstandard-2.1). Setting this value in the connection string does nothing, but the behaviour is supported.
| `Charset`                                                                                  | &#10003; | If not specified, the server should dictate the character set
| `ClientHostName`                                                                           | &#10003;
| `ClientHostProc`                                                                           | &#10003;
| `CodePageType`                                                                             | &#10005; | This doesn't appear to be relevant any more. You can specify the `Charset` without reference to a code page type, or allow the server to set the `Charset` which is the default behaviour.
| `Connection Lifetime` or `ConnectionLifetime`                                              | &#10003;
| `ConnectionIdleTimeout` or `Connection IdleTimeout` or `Connection Idle Timeout`           | &#10003;
| `CumulativeRecordCount`                                                                    | TODO
| `Database` or `Db` or `Initial Catalog`                                                    | &#10003;
| `Data Source` or `DataSource` or `Address` or `Addr` or `Network Address` or `Server Name` | &#10003;
| `DSURL` or `Directory Service URL`                                                         | &#10003; | Multiple URLs are not supported; network drivers other than NLWNSCK (TCP/IP socket) are not supported; LDAP is not supported
| `EnableServerPacketSize`                                                                   | &#10003;
| `Encryption`                                                                               | &#10003; | The designated encryption. Possible values: ssl, none.
| `EncryptPassword`                                                                          | &#10003; | Values 0 (disabled) and 1 (enabled) are supported. The highest encryption standard of the ASE 15.x and 16x servers is implemented.
| `LoginTimeOut` or `Connect Timeout` or `Connection Timeout`                                | &#10003; | For pooled connections this translates to the time it takes to reserve a connection from the pool
| `Max Pool Size`                                                                            | &#10003;
| `Min Pool Size`                                                                            | &#10003; | <ul><li>The pool will attempt to prime itself on creation up to this size (in a thread)</li><li>When a connection is killed, the pool will attempt to replace it if the pool size is less than Min</li></ul>
| `NamedParameters`                                                                          | &#10003;
| `PacketSize` or `Packet Size`                        |                                      &#10003; | The server can decide to change this value
| `Ping Server`                                                                              | &#10003;
| `Pooling`                                                                                  | &#10003;
| `Port` or `Server Port`                                                                    | &#10003;
| `Pwd` or `Password`                                                                        | &#10003;
| `TextSize`                                                                                 | &#10003;
| `TrustedFile`                                                                              | &#10003; | This property must be used along with `Encryption=ssl`. The value must be set to the path to the trusted file.
| `Uid` or `UserID` or `User ID` or `User`                                                   | &#10003;
| `UseAseDecimal`                                                                            | &#10003;

## Supported types
### Types supported when sending requests to the database

| DbType                  | Send      | .NET Type(s) | Notes
| ----------------------- |:---------:| ------------ | -----
| `AnsiString`            | &#10003;  | `string`
| `AnsiStringFixedLength` | &#10003;  | `string`
| `Binary`                | &#10003;  | `byte[]`
| `Boolean`               | &#10003;  | `bool`
| `Byte`                  | &#10003;  | `byte`
| `Currency`              | &#10003;  | `decimal` | Sent as decimal type; may change to send as `TDS_MONEY`, which is shorter
| `Date`                  | &#10003;  | `DateTime` | Time component is ignored
| `DateTime`              | &#10003;  | `DateTime`
| `DateTime2`             | X         | | ASE does not support a `DateTime2` type. Use `DateTime` instead
| `DateTimeOffset`        | X         | | ASE does not support a `DateTimeOffset` type. Use `DateTime` instead
| `Decimal`               | &#10003;  | `decimal`
| `Double`                | &#10003;  | `double`
| `Guid`                  | &#10003;  | `System.Guid` | Technically ASE does not support GUID or UUID types. Our driver supports it, but converts to `Binary` under the hood. You can obtain the same result by calling `.ToByteArray()` and using `DbType.Binary`.
| `Int16`                 | &#10003;  | `short`
| `Int32`                 | &#10003;  | `int`
| `Int64`                 | &#10003;  | `long`
| `Object`                | X         | | ASE does not support an `Object` type
| `SByte`                 | &#10003;  | `sbyte` | Sent as int16
| `Single`                | &#10003;  | `float`
| `String`                | &#10003;  | `string` | UTF-16 encoded, sent to server as binary with usertype `35`
| `StringFixedLength`     | &#10003;  | `string` | UTF-16 encoded, sent to server as binary with usertype `34`
| `Time`                  | &#10003;  | `TimeSpan`
| `UInt16`                | &#10003;  | `ushort`
| `UInt32`                | &#10003;  | `uint`
| `UInt64`                | &#10003;  | `ulong`
| `VarNumeric`            | &#10003;  | `decimal`
| `Xml`                   | X         | | ASE does not support an `Xml` type

### Types supported when reading responses from the database

| ASE Type            | Receive   | .NET Type(s) | Notes
| ------------------- |:---------:| ------------ | -----
| `bigdatetime`       | X         | `DateTime` | To be implemented. `TDS_BIGDATETIME = 0xBB`
| `bigint`            | &#10003;  | `long`
| `bigtime`           | X         | `DateTime` | To be implemented. `TDS_BIGTIME = 0xBC`
| `binary`            | &#10003;  | `byte[]`
| `bit`               | &#10003;  | `bool`
| `char`              | &#10003;  | `string`
| `date`              | &#10003;  | `DateTime`
| `datetime`          | &#10003;  | `DateTime`
| `decimal`           | &#10003;  | `decimal`
| `double precision`  | &#10003;  | `double`
| `float`             | &#10003;  | `float`
| `image`             | &#10003;  | `byte[]`
| `int`               | &#10003;  | `int`
| `money`             | &#10003;  | `decimal`
| `nchar`             | &#10003;  | `string`
| `numeric`           | &#10003;  | `decimal`
| `nvarchar`          | &#10003;  | `string`
| `smalldatetime`     | &#10003;  | `DateTime`
| `smallint`          | &#10003;  | `short`
| `smallmoney`        | &#10003;  | `decimal`
| `time`              | &#10003;  | `DateTime` | We have added a `GetTimeSpan` method to `AseDataReader`
| `tinyint`           | &#10003;  | `byte`
| `unichar`           | &#10003;  | `string` | Server sends as binary with usertype `34`
| `univarchar`        | &#10003;  | `string` | Server sends as binary with usertype `35`
| `unsigned bigint`   | &#10003;  | `ulong`
| `unsigned int`      | &#10003;  | `uint`
| `unsigned smallint` | &#10003;  | `usmallint`
| `varchar`           | &#10003;  | `string`
| `text`              | &#10003;  | `string`
| `unitext`           | &#10003;  | `string`
| `varbinary`         | &#10003;  | `byte[]`

## Code samples
### Open a database connection
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using(var connection = new AseConnection(connectionString))
{
    connection.Open();

    // use the connection...
}
```

### Execute a SQL statement and read response data
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString))
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT FirstName, LastName FROM Customer";

        using (var reader = command.ExecuteReader())
        {
            // Get the results.
            while (reader.Read())
            {
                var firstName = reader.GetString(0);
                var lastName = reader.GetString(1);

                // Do something with the data...
            }
        }
    }
}
```

### Execute a SQL statement that returns no results
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString))
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "INSERT INTO Customer (FirstName, LastName) VALUES ('Fred', 'Flintstone')";

        var recordsModified = command.ExecuteNonQuery();
    }
}
```

### Execute a SQL statement that returns a scalar value
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString))
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT COUNT(*) FROM Customer";

        var result = command.ExecuteScalar();
    }
}
```

### Use input parameters with a SQL query
Note: ASE only allows `Output`, `InputOutput`, and `ReturnValue` parameters with stored procedures
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString)
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT TOP 1 FirstName FROM Customer WHERE LastName = @lastName";

        command.Parameters.AddWithValue("@lastName", "Rubble");

        var result = command.ExecuteScalar();
    }
}
```

### Execute a stored procedure and read response data
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString)
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "GetCustomer";
        command.CommandType = CommandType.StoredProcedure;

        command.Parameters.AddWithValue("@lastName", "Rubble");

        using (var reader = command.ExecuteReader())
        {
            // Get the results.
            while (reader.Read())
            {
                var firstName = reader.GetString(0);
                var lastName = reader.GetString(1);

                // Do something with the data...
            }
        }
    }
}
```

### Execute a stored procedure that returns no results
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString))
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "CreateCustomer";
        command.CommandType = CommandType.StoredProcedure;

        command.Parameters.AddWithValue("@firstName", "Fred");
        command.Parameters.AddWithValue("@lastName", "Flintstone");

        command.ExecuteNonQuery();
    }
}
```

### Execute a stored procedure that returns a scalar value
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString))
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "CountCustomer";
        command.CommandType = CommandType.StoredProcedure;

        var result = command.ExecuteScalar();
    }
}
```

### Use input, output, and return parameters with a stored procedure
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString))
{
    connection.Open();

    using (var command = connection.CreateCommand())
    {
        command.CommandText = "GetCustomerFirstName";
        command.CommandType = CommandType.StoredProcedure;

        command.Parameters.AddWithValue("@lastName", "Rubble");

        var outputParameter = command.Parameters.Add("@firstName", AseDbType.VarChar);
        outputParameter.Direction = ParameterDirection.Output;

        var returnParameter = command.Parameters.Add("@returnValue", AseDbType.Integer);
        returnParameter.Direction = ParameterDirection.ReturnValue;

        command.ExecuteNonQuery();

        //Do something with outputParameter.Value and returnParameter.Value...
    }
}
```

### Execute a stored procedure and read response data with [Dapper](https://github.com/StackExchange/Dapper)
```C#
var connectionString = "Data Source=myASEserver;Port=5000;Database=myDataBase;Uid=myUsername;Pwd=myPassword;";

using (var connection = new AseConnection(connectionString))
{
    connection.Open();

    var barneyRubble = connection.Query<Customer>("GetCustomer", new {lastName = "Rubble"}, commandType: CommandType.StoredProcedure).First();

    // Do something with the result...
}
```
