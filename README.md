# iModel Import Console

A .NET 8 console application that imports element data and properties from a Bentley iTwin iModel into a PostgreSQL staging database, enabling downstream asset register workflows.

---

## Overview

The application connects to the [Bentley iTwin Platform APIs](https://developer.bentley.com), queries 3D geometric element classes from an iModel using ECSql, resolves property mappings from a Core database, converts element origin coordinates to WGS84, and stages the results into a PostgreSQL staging database.

---

## Architecture

```
iModel (Bentley iTwin Platform)
        │
        ▼
  IModelApiClient          ← ECSql queries + coordinate conversion
        │
        ▼
  ECSqlQueryBuilder        ← Builds element & aspect queries
        │
        ▼
  ArsPropertyResolver      ← Resolves property names from local ARS service
        │
        ▼
  CoreDbService            ← Reads mappings from Core DB, writes to Staging DB
        │
        ▼
  PostgreSQL Staging DB
  ├── staging_import_features
  ├── staging_import_feature_properties
  └── staging_import_feature_geometries
```

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Access to a Bentley iTwin Platform tenant with `imodels:read` and `itwin-platform` scopes
- PostgreSQL database (Core DB + Staging DB)
- Local ARS (Asset Register Service) running on `http://localhost:5001` *(optional — falls back to Core DB names)*

---

## Configuration

Copy `appsettings.json` and create `appsettings.Development.json` for your local overrides:

```json
{
  "Environments": {
    "dev": {
      "Authority": "https://qa-ims.bentley.com",
      "ApiBaseUrl": "https://dev-api.bentley.com",
      "ClientId": "<your-client-id>",
      "ClientSecret": "<your-client-secret>",
      "Scopes": [ "itwin-platform", "imodels:read" ]
    },
    "qa": {
      "Authority": "https://qa-ims.bentley.com",
      "ApiBaseUrl": "https://qa-api.bentley.com",
      "ClientId": "<your-client-id>",
      "ClientSecret": "<your-client-secret>",
      "Scopes": [ "imodels:read", "itwin-platform" ]
    }
  },
  "Database": {
    "CoreDb": "Host=...;Database=dataimportexport_core;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true",
    "StagingDb": "Host=...;Database=dataimportexport_staging;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true"
  },
  "Defaults": {
    "ITwinId": "<your-itwin-id>",
    "IModelId": "<your-imodel-id>",
    "ChangesetId": "<your-changeset-id>"
  }
}
```

> `appsettings.Development.json` is excluded from source control via `.gitignore`. Never commit real credentials.

---

## Build & Run

```powershell
# Build
dotnet build

# Run against dev environment
dotnet run -- --env dev

# Run against qa environment
dotnet run -- --env qa

# Run with a pre-obtained Bearer token (skips auth)
dotnet run -- --env dev --token "eyJ..."

# Show all 3D geometric classes (no filter)
dotnet run -- --env dev --show-all
```

---

## How It Works

1. **Authenticate** — Acquires a Bearer token via Client Credentials flow from Bentley IMS.
2. **Discover Classes** — Queries all `BisCore.GeometricElement3d` subclasses in the iModel.
3. **Check Mappings** — For each class, looks up a matching `data_segment` in the Core DB. Classes without a mapping are skipped.
4. **Resolve Hierarchy** — Finds the `DataTemplate → DataTemplateSubmission → FeatureTypeSubmission` chain.
5. **Fetch Element Data** — Queries element properties (including aspect properties) from the iModel via ECSql (up to 1,000 elements per class).
6. **Convert Coordinates** — Calls the iModel Coordinate API to convert each element's `Origin` (local iModel coordinates) to WGS84 (Lat/Lon/Elevation).
7. **Stage Data** — Writes to PostgreSQL:
   - `staging_import_features` — one row per element
   - `staging_import_feature_properties` — one row per mapped property value
   - `staging_import_feature_geometries` — WKT `POINT` geometry in SRID 4326

---

## Project Structure

```
iModel-Import-Console/
├── Auth/
│   └── TokenManager.cs          # Client credentials token acquisition & refresh
├── Config/
│   └── EnvironmentConfig.cs     # Configuration model binding
├── Services/
│   ├── ArsPropertyResolver.cs   # Resolves property & feature type names from ARS
│   ├── CoreDbService.cs         # Core DB reads + Staging DB writes (Dapper/Npgsql)
│   ├── ECSqlQueryBuilder.cs     # Builds ECSql queries for elements and aspects
│   └── IModelApiClient.cs       # Bentley iTwin API client (ECSql + coordinate conversion)
├── Program.cs                   # Main entry point and orchestration logic
├── appsettings.json             # Base configuration (placeholder credentials)
├── iModelImportConsole.csproj   # Project file (.NET 8)
└── nuget.config                 # NuGet source configuration
```

---

## Key Dependencies

| Package | Purpose |
|---|---|
| `Npgsql` | PostgreSQL driver |
| `Dapper` | Lightweight ORM for DB queries |
| `Microsoft.Extensions.Configuration` | JSON config binding |
| `System.CommandLine` | CLI argument parsing (`--env`, `--token`, `--show-all`) |

---

## Notes

- The application processes up to **1,000 elements per class** per run. Adjust `maxElements` in `Program.cs` if needed.
- Geometry is only inserted if the iModel has a Geographic Coordinate System (GCS) defined. If the coordinate conversion API returns no result (e.g. `NoGCSDefined`), geometry is skipped for all elements and a warning is logged once.
- Property values are staged with both `source_value` (raw iModel value) and `final_value` (after applying default value / formula transformations).
