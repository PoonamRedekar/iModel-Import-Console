namespace iModelImportConsole.Services;

/// <summary>
/// Builds ECSql queries for retrieving element property values from iModels.
/// Handles both element-level properties and aspect-level properties:
/// - Element properties: queried directly from the element class
/// - Aspect properties: queried from their respective aspect classes via Element.Id linkage
/// </summary>
public static class ECSqlQueryBuilder
{
    /// <summary>
    /// Builds the ECSql metadata query to discover property kinds for a given class
    /// (inheritance chain only — does NOT include aspect properties).
    /// </summary>
    public static string BuildPropertyMetadataQuery(string schemaName, string className)
    {
        return $@"SELECT
  p.Name AS PropertyName,
  p.Kind AS PropertyKind,
  p.PrimitiveType AS PrimitiveType
FROM ECDbMeta.ECPropertyDef p
JOIN ECDbMeta.ECClassDef ownerClass ON ownerClass.ECInstanceId = p.Class.Id
JOIN ECDbMeta.ECSchemaDef ownerSchema ON ownerSchema.ECInstanceId = ownerClass.Schema.Id
JOIN ECDbMeta.ECClassDef targetClass ON targetClass.Name = '{className}'
JOIN ECDbMeta.ECSchemaDef targetSchema ON targetSchema.ECInstanceId = targetClass.Schema.Id
  AND (targetSchema.Name = '{schemaName}' OR targetSchema.Alias = '{schemaName}')
JOIN ECDbMeta.ClassHasAllBaseClasses abc ON abc.SourceECInstanceId = targetClass.ECInstanceId
  AND abc.TargetECInstanceId = ownerClass.ECInstanceId
WHERE ownerClass.Name <> 'ExternalSourceAspect'
ORDER BY p.Name";
    }

    /// <summary>
    /// Builds ECSql to discover unique aspect classes attached to elements of the given class.
    /// Discovers aspect classes (both UniqueAspect and MultiAspect) attached to elements.
    /// Uses ElementOwnsUniqueAspect relationship with the element's ECClassId.
    /// Returns: AspectSchemaName, AspectClassName, AspectClassId
    /// </summary>
    public static string BuildAspectDiscoveryQuery(string schemaName, string className, int sampleLimit = 100)
    {
        return $@"SELECT DISTINCT
  ec_classname(a.ECClassId, 's') AS AspectSchemaName,
  ec_classname(a.ECClassId, 'c') AS AspectClassName,
  CAST(a.ECClassId AS TEXT) AS AspectClassId
FROM BisCore.ElementOwnsUniqueAspect rel
JOIN BisCore.ElementUniqueAspect a ON a.ECInstanceId = rel.TargetECInstanceId
WHERE rel.SourceECInstanceId IN (
  SELECT e.ECInstanceId FROM [{schemaName}].[{className}] e LIMIT {sampleLimit}
)
AND ec_classname(a.ECClassId, 'c') <> 'ExternalSourceAspect'

UNION

SELECT DISTINCT
  ec_classname(a.ECClassId, 's') AS AspectSchemaName,
  ec_classname(a.ECClassId, 'c') AS AspectClassName,
  CAST(a.ECClassId AS TEXT) AS AspectClassId
FROM BisCore.ElementOwnsMultiAspects rel
JOIN BisCore.ElementMultiAspect a ON a.ECInstanceId = rel.TargetECInstanceId
WHERE rel.SourceECInstanceId IN (
  SELECT e.ECInstanceId FROM [{schemaName}].[{className}] e LIMIT {sampleLimit}
)
AND ec_classname(a.ECClassId, 'c') <> 'ExternalSourceAspect'";
    }

    /// <summary>
    /// Builds ECSql to get all properties of a specific aspect class by its ClassId.
    /// Returns: PropertyName, DisplayLabel, PropertyKind, PrimitiveType
    /// </summary>
    public static string BuildAspectPropertyMetadataQueryByClassId(string aspectClassId)
    {
        return $@"SELECT
  p.Name AS PropertyName,
  p.DisplayLabel AS DisplayLabel,
  p.PrimitiveType AS PrimitiveType,
  p.Kind AS PropertyKind
FROM ECDbMeta.ECPropertyDef p
WHERE p.Class.Id = {aspectClassId}
ORDER BY p.Name";
    }


    /// <summary>
    /// Builds ECSql to query aspect property values.
    /// Returns Element.Id + requested properties from the aspect class.
    /// </summary>
    public static (string Query, string[] PropertyNames) BuildAspectQuery(
        string aspectSchema, string aspectClassName,
        List<string> propertyNames,
        Dictionary<string, int> propertyKinds,
        int limit = 1000,
        string? elementSchema = null,
        string? elementClassName = null)
    {
        var selectParts = new List<string> { "a.Element.Id AS [elementId]" };
        var outputNames = new List<string> { "elementId" };

        foreach (var propName in propertyNames)
        {
            var kind = propertyKinds.GetValueOrDefault(propName, -1);

            // If the full name (including underscores) is a known property, use it directly
            if (kind >= 0)
            {
                switch (kind)
                {
                    case 4: // Navigation
                        selectParts.Add($"a.[{propName}].Id AS [{propName}]");
                        break;
                    default:
                        selectParts.Add($"a.[{propName}] AS [{propName}]");
                        break;
                }
                outputNames.Add(propName);
                continue;
            }

            // Struct sub-properties (underscore notation) — only if NOT a known property
            if (propName.Contains('_'))
            {
                var parts = propName.Split('_');
                var suffix = parts[^1];
                var fullSuffix = string.Join("_", parts.Skip(1));

                if (propertyKinds.ContainsKey(fullSuffix))
                {
                    selectParts.Add($"a.[{fullSuffix}] AS [{propName}]");
                    outputNames.Add(propName);
                }
                else if (propertyKinds.ContainsKey(suffix))
                {
                    selectParts.Add($"a.[{suffix}] AS [{propName}]");
                    outputNames.Add(propName);
                }
                else
                {
                    // Fallback: treat as direct property (not dot path)
                    selectParts.Add($"a.[{propName}] AS [{propName}]");
                    outputNames.Add(propName);
                }
                continue;
            }

            // Unknown property without underscore — try direct
            selectParts.Add($"a.[{propName}] AS [{propName}]");
            outputNames.Add(propName);
        }

        var selectClause = string.Join(", ", selectParts);
        var whereClause = "";
        if (!string.IsNullOrEmpty(elementSchema) && !string.IsNullOrEmpty(elementClassName))
        {
            whereClause = $"\nWHERE a.Element.Id IN (SELECT ECInstanceId FROM [{elementSchema}].[{elementClassName}])";
        }
        var query = $@"SELECT {selectClause}
FROM [{aspectSchema}].[{aspectClassName}] a{whereClause}
LIMIT {limit}";

        return (query, outputNames.ToArray());
    }

    /// <summary>
    /// Builds the element values query using discovered property metadata.
    /// Only for element-level properties (from the class hierarchy).
    /// </summary>
    public static (string Query, string[] PropertyNames) BuildElementQuery(
        string schema, string className,
        List<string> requestedPropertyNames,
        Dictionary<string, int> propertyKinds,
        Dictionary<string, int>? primitiveTypes = null,
        int limit = 50)
    {
        primitiveTypes ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var selectParts = new List<string>();
        var outputNames = new List<string>();

        // Always start with ECInstanceId
        selectParts.Add("e.ECInstanceId AS [ECInstanceId]");
        outputNames.Add("ECInstanceId");

        foreach (var propName in requestedPropertyNames)
        {
            if (propName.Equals("ECInstanceId", StringComparison.OrdinalIgnoreCase))
                continue;

            if (propName.Equals("ECClassId", StringComparison.OrdinalIgnoreCase))
            {
                selectParts.Add("e.ECClassId AS [ECClassId]");
                outputNames.Add("ECClassId");
                continue;
            }

            // Struct sub-properties stored with underscore → dot notation
            // But only if the full name is NOT already a known property
            if (propName.Contains('_'))
            {
                var fullKind = propertyKinds.GetValueOrDefault(propName, -99);
                if (fullKind != -99)
                {
                    // Full underscore name is a real property — use directly
                    if (fullKind == 4)
                        selectParts.Add($"e.[{propName}].Id AS [{propName}]");
                    else
                        selectParts.Add($"e.[{propName}] AS [{propName}]");
                    outputNames.Add(propName);
                    continue;
                }

                var parts = propName.Split('_');
                var parentName = parts[0];
                var parentKind = propertyKinds.GetValueOrDefault(parentName, -1);
                if (parentKind == 1 || parentKind == -1)
                {
                    var dotPath = string.Join(".", parts);
                    selectParts.Add($"e.{dotPath} AS [{propName}]");
                    outputNames.Add(propName);
                }
                continue;
            }

            // Look up property kind from metadata
            var kind = propertyKinds.GetValueOrDefault(propName, -1);

            // Override kind for well-known geometric struct properties
            if (IsPointStruct(propName) && kind != 1)
                kind = 1;

            switch (kind)
            {
                case 4: // Navigation → select the .Id directly (ECSql doesn't support JOINs)
                    if (propName.Equals("Model", StringComparison.OrdinalIgnoreCase))
                        selectParts.Add($"e.Model.Id AS [{propName}]");
                    else if (propName.Equals("Category", StringComparison.OrdinalIgnoreCase))
                        selectParts.Add($"e.Category.Id AS [{propName}]");
                    else if (propName.Equals("Parent", StringComparison.OrdinalIgnoreCase))
                        selectParts.Add($"e.Parent.Id AS [{propName}]");
                    else if (propName.Equals("CodeScope", StringComparison.OrdinalIgnoreCase))
                        selectParts.Add($"e.CodeScope.Id AS [{propName}]");
                    else if (propName.Equals("CodeSpecification", StringComparison.OrdinalIgnoreCase))
                        selectParts.Add($"e.CodeSpecification.Id AS [{propName}]");
                    else if (propName.Equals("TypeDefinition", StringComparison.OrdinalIgnoreCase))
                        selectParts.Add($"e.TypeDefinition.Id AS [{propName}]");
                    else
                        selectParts.Add($"e.[{propName}].Id AS [{propName}]");
                    outputNames.Add(propName);
                    break;

                case 1: // Struct → expand to sub-properties
                    if (IsPointStruct(propName))
                    {
                        selectParts.Add($"e.{propName}.X AS [{propName}__X]");
                        selectParts.Add($"e.{propName}.Y AS [{propName}__Y]");
                        selectParts.Add($"e.{propName}.Z AS [{propName}__Z]");
                        outputNames.Add($"{propName}__X");
                        outputNames.Add($"{propName}__Y");
                        outputNames.Add($"{propName}__Z");
                    }
                    else if (propName.Equals("Placement", StringComparison.OrdinalIgnoreCase))
                    {
                        selectParts.Add($"e.Placement.Origin.X AS [Placement__X]");
                        selectParts.Add($"e.Placement.Origin.Y AS [Placement__Y]");
                        selectParts.Add($"e.Placement.Origin.Z AS [Placement__Z]");
                        outputNames.Add("Placement__X");
                        outputNames.Add("Placement__Y");
                        outputNames.Add("Placement__Z");
                    }
                    else
                    {
                        selectParts.Add($"e.[{propName}] AS [{propName}]");
                        outputNames.Add(propName);
                    }
                    break;
                case 2: // PrimitiveArray → return as whole
                case 3: // StructArray → return as whole
                    selectParts.Add($"e.[{propName}] AS [{propName}]");
                    outputNames.Add(propName);
                    break;

                case 0:  // Primitive → direct select
                default: // Not in metadata or unknown → try as primitive
                    selectParts.Add($"e.{propName} AS [{propName}]");
                    outputNames.Add(propName);
                    break;
            }
        }

        var selectClause = string.Join(", ", selectParts);

        var query = $@"SELECT {selectClause}
FROM [{schema}].[{className}] e
LIMIT {limit}";

        return (query, outputNames.ToArray());
    }

    private static bool IsPointStruct(string propName)
    {
        var pointProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Origin", "BBoxLow", "BBoxHigh"
        };
        return pointProps.Contains(propName);
    }
}
