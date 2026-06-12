using Dapper;
using Npgsql;
using iModelImportConsole.Config;

namespace iModelImportConsole.Services;

/// <summary>
/// Reads property mappings from Core DB and writes staged data to Staging DB.
/// </summary>
public class CoreDbService
{
    private readonly string _coreConnectionString;
    private readonly string _stagingConnectionString;

    public CoreDbService(DatabaseConfig dbConfig)
    {
        _coreConnectionString = dbConfig.CoreDb;
        _stagingConnectionString = dbConfig.StagingDb;
    }

    // ─── Core DB reads ───

    /// <summary>
    /// Find data segments for the given iTwin that match a specific iModel class name in metadata.
    /// </summary>
    public async Task<DataSegmentInfo?> FindDataSegmentByClassNameAsync(Guid iTwinId, string className)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT ds.id AS Id, ds.name AS Name, ds.display_label AS DisplayLabel,
                   ds.feature_type_id AS FeatureTypeId, ds.metadata AS Metadata,
                   ds.data_source_type_id AS DataSourceTypeId
            FROM data_segments ds
            WHERE ds.itwin_id = @iTwinId
              AND ds.metadata::jsonb->>'className' = @ClassName
            LIMIT 1";

        // Try exact match first
        var result = await conn.QueryFirstOrDefaultAsync<DataSegmentInfo>(sql, new { iTwinId, ClassName = className });
        if (result != null) return result;

        // Try with decoded backslashes: Bearing__x005C__Bearing → Bearing\Bearing
        var decoded = className.Replace("__x005C__", "\\");
        if (decoded != className)
        {
            result = await conn.QueryFirstOrDefaultAsync<DataSegmentInfo>(sql, new { iTwinId, ClassName = decoded });
            if (result != null) return result;
        }

        // Try last segment only: Supports__x005C__Pier__x005C__Pier_concrete_piles → Pier_concrete_piles
        var lastSegment = decoded.Contains('\\') ? decoded.Split('\\').Last() : null;
        if (!string.IsNullOrEmpty(lastSegment))
        {
            result = await conn.QueryFirstOrDefaultAsync<DataSegmentInfo>(sql, new { iTwinId, ClassName = lastSegment });
            if (result != null) return result;
        }

        return null;
    }

    /// <summary>
    /// Get all data segments for the given iTwin that have an iModel class name in their metadata.
    /// </summary>
    public async Task<List<DataSegmentInfo>> GetAllDataSegmentsAsync(Guid iTwinId)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT ds.id AS Id, ds.name AS Name, ds.display_label AS DisplayLabel,
                   ds.feature_type_id AS FeatureTypeId, ds.metadata AS Metadata,
                   ds.data_source_type_id AS DataSourceTypeId
            FROM data_segments ds
            WHERE ds.itwin_id = @iTwinId
              AND ds.metadata IS NOT NULL
              AND ds.metadata::jsonb->>'className' IS NOT NULL
            ORDER BY ds.name";
        return (await conn.QueryAsync<DataSegmentInfo>(sql, new { iTwinId })).ToList();
    }

    /// <summary>
    /// Get all data segment properties for a segment.
    /// </summary>
    public async Task<List<DataSegmentPropertyInfo>> GetSegmentPropertiesAsync(Guid iTwinId, Guid dataSegmentId)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT id AS Id, name AS Name, data_segment_id AS DataSegmentId,
                   sequence AS Sequence, is_geometry AS IsGeometry
            FROM data_segment_properties
            WHERE itwin_id = @iTwinId AND data_segment_id = @DataSegmentId
            ORDER BY sequence";
        return (await conn.QueryAsync<DataSegmentPropertyInfo>(sql, new { iTwinId, DataSegmentId = dataSegmentId })).ToList();
    }

    /// <summary>
    /// Get all property mappings for a segment.
    /// </summary>
    public async Task<List<PropertyMappingInfo>> GetPropertyMappingsAsync(Guid iTwinId, Guid dataSegmentId)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT m.id AS Id, m.target_property_id AS TargetPropertyId,
                   m.data_segment_id AS DataSegmentId, m.data_segment_property_id AS DataSegmentPropertyId,
                   m.default_value AS DefaultValue, m.duplicate_detection AS DuplicateDetection,
                   m.use_current_date AS UseCurrentDate, m.formula AS Formula
            FROM data_segment_property_mappings m
            WHERE m.itwin_id = @iTwinId AND m.data_segment_id = @DataSegmentId";
        return (await conn.QueryAsync<PropertyMappingInfo>(sql, new { iTwinId, DataSegmentId = dataSegmentId })).ToList();
    }

    /// <summary>
    /// Get the feature type submission for a segment's feature type.
    /// </summary>
    public async Task<FeatureTypeSubmissionInfo?> GetFeatureTypeSubmissionAsync(Guid iTwinId, Guid featureTypeId)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT fts.id AS Id, fts.data_template_submission_id AS DataTemplateSubmissionId,
                   fts.feature_type_id AS FeatureTypeId, fts.status AS Status, fts.bucket_id AS BucketId
            FROM feature_type_submissions fts
            WHERE fts.itwin_id = @iTwinId AND fts.feature_type_id = @FeatureTypeId
            LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<FeatureTypeSubmissionInfo>(sql, new { iTwinId, FeatureTypeId = featureTypeId });
    }

    /// <summary>
    /// Get the data template for a feature type (via feature_type → data_template).
    /// </summary>
    public async Task<DataTemplateInfo?> GetDataTemplateForFeatureTypeAsync(Guid iTwinId, Guid featureTypeId)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT dt.id AS Id, dt.name AS Name, dt.display_label AS DisplayLabel, dt.disabled AS Disabled
            FROM data_templates dt
            JOIN feature_types ft ON ft.data_template_id = dt.id AND ft.itwin_id = dt.itwin_id
            WHERE dt.itwin_id = @iTwinId AND ft.id = @FeatureTypeId
            LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<DataTemplateInfo>(sql, new { iTwinId, FeatureTypeId = featureTypeId });
    }

    /// <summary>
    /// Get feature type details including target_feature_type_id.
    /// </summary>
    public async Task<FeatureTypeInfo?> GetFeatureTypeAsync(Guid iTwinId, Guid featureTypeId)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT id AS Id, name AS Name, display_label AS DisplayLabel,
                   target_service AS TargetService, target_feature_type_id AS TargetFeatureTypeId
            FROM feature_types
            WHERE itwin_id = @ITwinId AND id = @FeatureTypeId
            LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<FeatureTypeInfo>(sql, new { ITwinId = iTwinId, FeatureTypeId = featureTypeId });
    }

    /// <summary>
    /// Get the latest data template submission for a template.
    /// </summary>
    public async Task<DataTemplateSubmissionInfo?> GetDataTemplateSubmissionAsync(Guid iTwinId, Guid dataTemplateId)
    {
        await using var conn = new NpgsqlConnection(_coreConnectionString);
        var sql = @"
            SELECT id AS Id, name AS Name, display_label AS DisplayLabel, data_template_id AS DataTemplateId, status AS Status
            FROM data_template_submissions
            WHERE itwin_id = @iTwinId AND data_template_id = @DataTemplateId
            LIMIT 1";
        return await conn.QueryFirstOrDefaultAsync<DataTemplateSubmissionInfo>(sql, new { iTwinId, DataTemplateId = dataTemplateId });
    }

    // ─── Staging DB writes ───

    /// <summary>
    /// Insert a staging feature and return its ID.
    /// </summary>
    public async Task<Guid> InsertStagingFeatureAsync(Guid iTwinId, Guid featureTypeSubmissionId, short bucketId)
    {
        await using var conn = new NpgsqlConnection(_stagingConnectionString);
        var id = Guid.NewGuid();
        var sql = @"
            INSERT INTO staging_import_features (id, itwin_id, feature_type_submission_id, status, system_recommendation, bucket_id)
            VALUES (@Id, @ITwinId, @FeatureTypeSubmissionId, 'NEW', 'NEW', @BucketId)";
        await conn.ExecuteAsync(sql, new { Id = id, ITwinId = iTwinId, FeatureTypeSubmissionId = featureTypeSubmissionId, BucketId = bucketId });
        return id;
    }

    /// <summary>
    /// Insert a staging feature property.
    /// </summary>
    public async Task InsertStagingFeaturePropertyAsync(Guid iTwinId, Guid featureId, Guid targetPropertyId, short bucketId, string? sourceValue, string? finalValue)
    {
        await using var conn = new NpgsqlConnection(_stagingConnectionString);
        var id = Guid.NewGuid();
        var sql = @"
            INSERT INTO staging_import_feature_properties (id, itwin_id, feature_id, target_property_id, source_value, final_value, bucket_id)
            VALUES (@Id, @ITwinId, @FeatureId, @TargetPropertyId, @SourceValue, @FinalValue, @BucketId)";
        await conn.ExecuteAsync(sql, new
        {
            Id = id,
            ITwinId = iTwinId,
            FeatureId = featureId,
            TargetPropertyId = targetPropertyId,
            SourceValue = sourceValue,
            FinalValue = finalValue,
            BucketId = bucketId
        });
    }

    /// <summary>
    /// Insert a staging feature geometry into staging_import_feature_geometries.
    /// geometryType must be 'POINT', 'LINE', or 'POLYGON' (check constraint).
    /// wkt is a WKT string e.g. "POINT(lon lat alt)" or "POLYGON((lon1 lat1, ...))"
    /// </summary>
    public async Task InsertStagingFeatureGeometryAsync(
        Guid iTwinId, Guid featureId, short bucketId,
        string geometryType, string wkt)
    {
        await using var conn = new NpgsqlConnection(_stagingConnectionString);
        var id = Guid.NewGuid();
        var sql = @"
            INSERT INTO staging_import_feature_geometries
                (id, itwin_id, feature_id, geometry_type, source_geometry, bucket_id)
            VALUES
                (@Id, @ITwinId, @FeatureId, @GeometryType,
                 ST_SetSRID(ST_GeomFromText(@Wkt), 4326),
                 @BucketId)";
        await conn.ExecuteAsync(sql, new
        {
            Id = id,
            ITwinId = iTwinId,
            FeatureId = featureId,
            GeometryType = geometryType,
            Wkt = wkt,
            BucketId = bucketId
        });
    }

    // ─── Staging DB reads ───

    /// <summary>
    /// Get all staged features for a submission.
    /// </summary>
    public async Task<List<StagedFeatureInfo>> GetStagedFeaturesAsync(Guid iTwinId, Guid featureTypeSubmissionId)
    {
        await using var conn = new NpgsqlConnection(_stagingConnectionString);
        var sql = @"
            SELECT id AS Id, feature_type_submission_id AS FeatureTypeSubmissionId, status AS Status,
                   system_recommendation AS SystemRecommendation
            FROM staging_import_features
            WHERE itwin_id = @ITwinId AND feature_type_submission_id = @FeatureTypeSubmissionId
            ORDER BY id";
        return (await conn.QueryAsync<StagedFeatureInfo>(sql, new { iTwinId, FeatureTypeSubmissionId = featureTypeSubmissionId })).ToList();
    }

    /// <summary>
    /// Get staged feature properties.
    /// </summary>
    public async Task<List<StagedFeaturePropertyInfo>> GetStagedFeaturePropertiesAsync(Guid iTwinId, Guid featureId)
    {
        await using var conn = new NpgsqlConnection(_stagingConnectionString);
        var sql = @"
            SELECT id AS Id, feature_id AS FeatureId, target_property_id AS TargetPropertyId,
                   source_value AS SourceValue, final_value AS FinalValue
            FROM staging_import_feature_properties
            WHERE itwin_id = @ITwinId AND feature_id = @FeatureId";
        return (await conn.QueryAsync<StagedFeaturePropertyInfo>(sql, new { iTwinId, FeatureId = featureId })).ToList();
    }
}

// ─── DTOs ───

public record FeatureTypeInfo
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public string TargetService { get; init; } = string.Empty;
    public string TargetFeatureTypeId { get; init; } = string.Empty;
}

public record DataTemplateInfo
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public bool Disabled { get; init; }
}

public record DataTemplateSubmissionInfo
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public Guid DataTemplateId { get; init; }
    public string Status { get; init; } = string.Empty;
}

public record DataSegmentInfo
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public Guid FeatureTypeId { get; init; }
    public string? Metadata { get; init; }
    public Guid DataSourceTypeId { get; init; }
}

public record DataSegmentPropertyInfo
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid DataSegmentId { get; init; }
    public int Sequence { get; init; }
    public bool IsGeometry { get; init; }
}

public record PropertyMappingInfo
{
    public Guid Id { get; init; }
    public Guid TargetPropertyId { get; init; }
    public Guid DataSegmentId { get; init; }
    public Guid DataSegmentPropertyId { get; init; }
    public string? DefaultValue { get; init; }
    public bool DuplicateDetection { get; init; }
    public bool UseCurrentDate { get; init; }
    public string? Formula { get; init; }
}

public record FeatureTypeSubmissionInfo
{
    public Guid Id { get; init; }
    public Guid DataTemplateSubmissionId { get; init; }
    public Guid FeatureTypeId { get; init; }
    public string Status { get; init; } = string.Empty;
    public short BucketId { get; init; }
}

public record StagedFeatureInfo
{
    public Guid Id { get; init; }
    public Guid FeatureTypeSubmissionId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string SystemRecommendation { get; init; } = string.Empty;
}

public record StagedFeaturePropertyInfo
{
    public Guid Id { get; init; }
    public Guid FeatureId { get; init; }
    public Guid TargetPropertyId { get; init; }
    public string? SourceValue { get; init; }
    public string? FinalValue { get; init; }
}
