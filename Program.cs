using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using iModelImportConsole.Auth;
using iModelImportConsole.Config;
using iModelImportConsole.Services;

// ─── Command-line options ───
var envOption = new Option<string>("--env", () => "dev", "Environment: dev or qa");
var tokenOption = new Option<string?>("--token", "Pre-obtained Bearer token (bypasses auth)");
var showAllOption = new Option<bool>("--show-all", () => false, "Show all 3D geometric classes (no filter)");

var rootCommand = new RootCommand("iModel Import Console - Stage iModel data via property mappings")
{
    envOption, tokenOption, showAllOption
};

rootCommand.SetHandler(async (context) =>
{
    try
    {
        var env = context.ParseResult.GetValueForOption(envOption)!;
        var token = context.ParseResult.GetValueForOption(tokenOption);
        var showAll = context.ParseResult.GetValueForOption(showAllOption);
        var ct = context.GetCancellationToken();

        // ─── Load configuration ───
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var envConfig = new EnvironmentConfig();
        config.GetSection($"Environments:{env}").Bind(envConfig);
        if (string.IsNullOrEmpty(envConfig.Authority))
        {
            Console.WriteLine($"[ERROR] Environment '{env}' not found in appsettings.json");
            context.ExitCode = 1;
            return;
        }

        var dbConfig = new DatabaseConfig();
        config.GetSection("Database").Bind(dbConfig);

        var defaults = config.GetSection("Defaults");
        var iTwinId = Guid.Parse(defaults["ITwinId"]!);
        var iModelId = defaults["IModelId"]!;
        var changesetId = defaults["ChangesetId"]!;

        Console.WriteLine("╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("║       iModel Import Console - Data Staging          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════╝");
        Console.WriteLine($"  iTwinId:     {iTwinId}");
        Console.WriteLine($"  iModelId:    {iModelId}");
        Console.WriteLine($"  ChangesetId: {changesetId}");
        Console.WriteLine($"  Environment: {env.ToUpper()}");
        Console.WriteLine();

        // ─── Authenticate ───
        TokenManager tokenManager;
        if (!string.IsNullOrEmpty(token))
        {
            var cleanToken = token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? token["Bearer ".Length..].Trim() : token.Trim();
            tokenManager = new TokenManager(cleanToken);
            Console.WriteLine("[INFO] Using pre-obtained Bearer token");
        }
        else
        {
            Console.WriteLine("[INFO] Authenticating via client credentials...");
            var (initialToken, expiresIn) = await TokenManager.AcquireClientCredentialsTokenAsync(envConfig, ct);
            tokenManager = new TokenManager(envConfig, initialToken, expiresIn);
            Console.WriteLine($"[INFO] Token acquired (valid for {expiresIn / 60} minutes)");
        }

        // ─── Connect to iModel ───
        using var apiClient = new IModelApiClient(envConfig, tokenManager, iModelId, iTwinId.ToString(), changesetId);

        // ─── Step 1: Query all GeometricElement3d classes ───
        Console.WriteLine("\n[INFO] Querying GeometricElement3d classes from iModel...");

        var classRows = await apiClient.ExecuteECSqlAsync(@"
        SELECT DISTINCT
          ec_classname(e.ECClassId, 's') AS SchemaName,
          ec_classname(e.ECClassId, 'c') AS ClassName,
          c.DisplayLabel AS ClassDisplayLabel,
          COUNT(*) AS InstanceCount
        FROM BisCore.GeometricElement3d e
        JOIN ECDbMeta.ECClassDef c ON c.ECInstanceId = e.ECClassId
        GROUP BY e.ECClassId
        ORDER BY InstanceCount DESC",
            ct, new[] { "SchemaName", "ClassName", "ClassDisplayLabel", "InstanceCount" });

        if (classRows.Count == 0)
        {
            Console.WriteLine("[ERROR] No 3D geometric element classes found in this iModel.");
            context.ExitCode = 1;
            return;
        }

        // ─── Use all discovered classes — Core DB mapping check will skip unmatched ones ───
        List<Dictionary<string, object?>> filteredClasses;
        if (showAll)
        {
            filteredClasses = classRows;
            Console.WriteLine($"[INFO] Processing ALL {classRows.Count} concrete 3D geometric classes:");
        }
        else
        {
            filteredClasses = classRows;
            Console.WriteLine($"[INFO] Processing {filteredClasses.Count} classes (skipping any without a Core DB mapping):");
        }

        // ─── Display class list ───
        Console.WriteLine();
        Console.WriteLine("  #  | Schema                  | Class Name                                    | Instances");
        Console.WriteLine("  ---|-------------------------|-----------------------------------------------|----------");
        for (int i = 0; i < filteredClasses.Count; i++)
        {
            var schema = filteredClasses[i].GetValueOrDefault("SchemaName")?.ToString() ?? "";
            var cls = filteredClasses[i].GetValueOrDefault("ClassName")?.ToString() ?? "";
            var count = filteredClasses[i].GetValueOrDefault("InstanceCount")?.ToString() ?? "0";
            Console.WriteLine($"  {i + 1,2} | {schema,-23} | {cls,-45} | {count,8}");
        }

        // ─── Process all filtered classes ───
        Console.WriteLine($"\n[INFO] Processing {filteredClasses.Count} classes for submission...\n");

        for (int classIndex = 0; classIndex < filteredClasses.Count; classIndex++)
        {
            var selectedClass = filteredClasses[classIndex];
            var selectedSchema = selectedClass.GetValueOrDefault("SchemaName")?.ToString()!;
            var selectedClassName = selectedClass.GetValueOrDefault("ClassName")?.ToString()!;
            var selectedInstanceCount = Convert.ToInt64(selectedClass.GetValueOrDefault("InstanceCount") ?? 0);

            Console.WriteLine($"\n╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  [{classIndex + 1}/{filteredClasses.Count}] {selectedSchema}:{selectedClassName} ({selectedInstanceCount} instances)");
            Console.WriteLine($"╚══════════════════════════════════════════════════════════════════╝");

            // ─── Step 2: Retrieve properties for the selected class ───
            Console.WriteLine("[INFO] Retrieving properties from iModel...");

            var propRows = await apiClient.ExecuteECSqlAsync($@"
        SELECT
          p.Name AS PropertyName,
          p.DisplayLabel,
          p.PrimitiveType,
          p.Kind AS PropertyKind,
          ownerClass.Name AS OwnerClass,
          ownerSchema.Name AS OwnerSchema
        FROM ECDbMeta.ECPropertyDef p
        JOIN ECDbMeta.ECClassDef ownerClass ON ownerClass.ECInstanceId = p.Class.Id
        JOIN ECDbMeta.ECSchemaDef ownerSchema ON ownerSchema.ECInstanceId = ownerClass.Schema.Id
        JOIN ECDbMeta.ClassHasAllBaseClasses abc ON abc.TargetECInstanceId = ownerClass.ECInstanceId
        WHERE abc.SourceECInstanceId IN (
            SELECT ECInstanceId FROM ECDbMeta.ECClassDef
            WHERE Name = '{selectedClassName}'
              AND Schema.Id IN (SELECT ECInstanceId FROM ECDbMeta.ECSchemaDef WHERE Name = '{selectedSchema}')
        )
        AND ownerClass.Name <> 'ExternalSourceAspect'
        ORDER BY OwnerSchema, OwnerClass, p.Name",
                ct, new[] { "PropertyName", "DisplayLabel", "PrimitiveType", "PropertyKind", "OwnerClass", "OwnerSchema" });

            // Add system properties
            var allPropertyNames = new List<string> { "ECInstanceId", "ECClassId" };
            allPropertyNames.AddRange(propRows.Select(r => r.GetValueOrDefault("PropertyName")?.ToString() ?? "").Where(n => !string.IsNullOrEmpty(n)));

            Console.WriteLine($"[INFO] Found {allPropertyNames.Count} properties for {selectedClassName}");
            Console.WriteLine();

            // ─── Step 3: Check property mapping in Core DB ───
            Console.WriteLine("[INFO] Checking Core DB for property mapping...");

            var coreDb = new CoreDbService(dbConfig);
            var segment = await coreDb.FindDataSegmentByClassNameAsync(iTwinId, selectedClassName);

            if (segment == null)
            {
                Console.WriteLine($"[WARN] No property mapping found for class '{selectedClassName}' in Core DB — skipping.");
                continue;
            }

            Console.WriteLine($"[INFO] ✓ Property mapping FOUND!");
            Console.WriteLine($"       Data Segment: {segment.Name} ({segment.DisplayLabel})");
            Console.WriteLine($"       Segment ID:   {segment.Id}");

            var segmentProperties = await coreDb.GetSegmentPropertiesAsync(iTwinId, segment.Id);
            var mappings = await coreDb.GetPropertyMappingsAsync(iTwinId, segment.Id);

            Console.WriteLine($"       Properties:   {segmentProperties.Count}");
            Console.WriteLine($"       Mappings:     {mappings.Count}");
            Console.WriteLine();

            // Resolve target property names and feature type name from ARS
            var featureType = await coreDb.GetFeatureTypeAsync(iTwinId, segment.FeatureTypeId);
            var targetPropertyNames = new Dictionary<Guid, string>();
            string? arsFeatureTypeName = null;
            var targetPropertyIds = mappings
                .Select(m => m.TargetPropertyId)
                .Distinct()
                .ToList();
            if (targetPropertyIds.Count > 0)
            {
                try
                {
                    var arsToken = await tokenManager.GetValidTokenAsync(ct);
                    var arsResolver = new ArsPropertyResolver("http://localhost:5001", arsToken);
                    arsFeatureTypeName = await arsResolver.ResolveFeatureTypeNameAsync(iTwinId, segment.FeatureTypeId, ct);
                    targetPropertyNames = await arsResolver.ResolvePropertyNamesAsync(iTwinId, targetPropertyIds, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] ARS resolution failed: {ex.Message} — using source property names");
                }
            }

            // Show mapping details
            var featureTypeDisplay = arsFeatureTypeName ?? featureType?.DisplayLabel ?? featureType?.Name ?? segment.FeatureTypeId.ToString();
            var featureTypeSource = arsFeatureTypeName != null ? "ARS" : (featureType?.DisplayLabel != null ? "Core DB" : "ID");
            Console.WriteLine($"\n  Feature Type: {featureTypeDisplay} (source: {featureTypeSource})");
            Console.WriteLine($"  Feature Type ID:  {segment.FeatureTypeId}");
            Console.WriteLine("\n  Property Mappings:");
            Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────────────────────");
            Console.WriteLine($"  {"Source (iModel)",-30} {"Target (ARS Property)",-40} {"Flags",-20}");
            Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────────────────────");
            var propLookup = segmentProperties.ToDictionary(p => p.Id, p => p.Name);
            foreach (var mapping in mappings)
            {
                var srcName = propLookup.GetValueOrDefault(mapping.DataSegmentPropertyId, "?");
                var targetName = targetPropertyNames.GetValueOrDefault(mapping.TargetPropertyId, mapping.TargetPropertyId.ToString());
                var flags = new List<string>();
                if (mapping.DuplicateDetection) flags.Add("DupDetect");
                if (mapping.UseCurrentDate) flags.Add("UseCurrentDate");
                if (!string.IsNullOrEmpty(mapping.DefaultValue)) flags.Add($"Default='{mapping.DefaultValue}'");
                var flagStr = flags.Count > 0 ? string.Join(", ", flags) : "";
                Console.WriteLine($"    {srcName,-28} → {targetName,-38} {flagStr}");
            }

            // ─── Step 4: Retrieve template hierarchy ───
            Console.WriteLine("\n[INFO] Resolving submission hierarchy...");

            var dataTemplate = await coreDb.GetDataTemplateForFeatureTypeAsync(iTwinId, segment.FeatureTypeId);
            if (dataTemplate == null)
            {
                Console.WriteLine("[WARN] No Data Template found for this feature type — skipping.");
                continue;
            }
            if (dataTemplate.Disabled)
            {
                Console.WriteLine($"[WARN] Data Template '{dataTemplate.DisplayLabel}' is DISABLED — skipping.");
                continue;
            }

            var templateSubmission = await coreDb.GetDataTemplateSubmissionAsync(iTwinId, dataTemplate.Id);
            if (templateSubmission == null)
            {
                Console.WriteLine("[WARN] No Data Template Submission found — skipping.");
                continue;
            }

            var featureTypeSubmission = await coreDb.GetFeatureTypeSubmissionAsync(iTwinId, segment.FeatureTypeId);
            if (featureTypeSubmission == null)
            {
                Console.WriteLine("[WARN] No Feature Type Submission found for this feature type — skipping.");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine("  Submission Hierarchy:");
            Console.WriteLine("  ─────────────────────────────────────────────────────────────────");
            Console.WriteLine($"    Data Template:            {dataTemplate.DisplayLabel} ({dataTemplate.Name})");
            Console.WriteLine($"    Data Template Submission: {templateSubmission.DisplayLabel} (Status: {templateSubmission.Status})");
            Console.WriteLine($"    Feature Type Submission:  {featureTypeSubmission.Id} (Status: {featureTypeSubmission.Status})");
            Console.WriteLine($"    Data Segment:             {segment.DisplayLabel} ({segment.Name})");
            Console.WriteLine();

            Console.WriteLine($"\n[INFO] Using Feature Type Submission: {featureTypeSubmission.Id}");

            // ─── Step 6: Retrieve element property values from iModel ───
            Console.WriteLine($"\n[INFO] Retrieving element data from iModel ({selectedInstanceCount} elements)...");

            var mappedPropertyNames = segmentProperties.Select(p => p.Name).ToList();
            var maxElements = 1000;

            // Step 6a: Discover element class property kinds (inheritance chain only)
            var metadataQuery = ECSqlQueryBuilder.BuildPropertyMetadataQuery(selectedSchema, selectedClassName);
            var propertyKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var primitiveTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var metadataRows = await apiClient.ExecuteECSqlAsync(metadataQuery, ct, new[] { "PropertyName", "PropertyKind", "PrimitiveType" });
                foreach (var row in metadataRows)
                {
                    var name = row.GetValueOrDefault("PropertyName")?.ToString();
                    var kindVal = row.GetValueOrDefault("PropertyKind");
                    var primTypeVal = row.GetValueOrDefault("PrimitiveType");
                    if (!string.IsNullOrEmpty(name) && kindVal != null && int.TryParse(kindVal.ToString(), out var kind))
                    {
                        propertyKinds[name] = kind;
                        if (primTypeVal != null && int.TryParse(primTypeVal.ToString(), out var primType))
                            primitiveTypes[name] = primType;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not discover element property metadata: {ex.Message}");
            }

            // Step 6b: Discover aspect classes attached to elements of this class
            var aspectClasses = new List<(string Schema, string ClassName, string ClassId)>();

            try
            {
                var aspectQuery = ECSqlQueryBuilder.BuildAspectDiscoveryQuery(selectedSchema, selectedClassName);
                var aspectRows = await apiClient.ExecuteECSqlAsync(aspectQuery, ct,
                    new[] { "AspectSchemaName", "AspectClassName", "AspectClassId" });
                foreach (var row in aspectRows)
                {
                    var schema = row.GetValueOrDefault("AspectSchemaName")?.ToString();
                    var clsName = row.GetValueOrDefault("AspectClassName")?.ToString();
                    var classId = row.GetValueOrDefault("AspectClassId")?.ToString();
                    if (!string.IsNullOrEmpty(schema) && !string.IsNullOrEmpty(clsName) && !string.IsNullOrEmpty(classId))
                        aspectClasses.Add((schema, clsName, classId));
                }
                if (aspectClasses.Count > 0)
                    Console.WriteLine($"[INFO] Discovered {aspectClasses.Count} aspect classes: {string.Join(", ", aspectClasses.Select(a => $"{a.Schema}.{a.ClassName}"))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not discover aspect classes: {ex.Message}");
            }

            // Step 6c: Get properties for each aspect class by ClassId → build map of property → aspect class
            var aspectPropertyMap = new Dictionary<string, (string Schema, string ClassName)>(StringComparer.OrdinalIgnoreCase);
            var aspectPropertyKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var aspect in aspectClasses)
            {
                try
                {
                    var aspectMetaQuery = ECSqlQueryBuilder.BuildAspectPropertyMetadataQueryByClassId(aspect.ClassId);
                    var aspectMetaRows = await apiClient.ExecuteECSqlAsync(aspectMetaQuery, ct,
                        new[] { "PropertyName", "DisplayLabel", "PrimitiveType", "PropertyKind" });

                    foreach (var row in aspectMetaRows)
                    {
                        var name = row.GetValueOrDefault("PropertyName")?.ToString();
                        var kindVal = row.GetValueOrDefault("PropertyKind");
                        if (!string.IsNullOrEmpty(name) && kindVal != null && int.TryParse(kindVal.ToString(), out var kind))
                        {
                            aspectPropertyMap[name] = (aspect.Schema, aspect.ClassName);
                            aspectPropertyKinds[name] = kind;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Could not get properties for aspect {aspect.Schema}.{aspect.ClassName}: {ex.Message}");
                }
            }

            // Step 6d: Separate mapped properties into element-level vs aspect-level
            var elementPropertyNames = new List<string>();
            var aspectPropertiesByClass = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var propName in mappedPropertyNames)
            {
                var lookupName = propName.Contains('_') ? propName.Split('_')[0] : propName;
                var suffixName = propName.Contains('_') ? propName.Split('_', 2)[1] : null;

                if (aspectPropertyMap.TryGetValue(lookupName, out var aspectInfo))
                {
                    var key = $"{aspectInfo.Schema}.{aspectInfo.ClassName}";
                    if (!aspectPropertiesByClass.ContainsKey(key))
                        aspectPropertiesByClass[key] = new List<string>();
                    aspectPropertiesByClass[key].Add(propName);
                }
                else if (propName.Contains('_') && aspectPropertyMap.TryGetValue(propName, out var aspectInfo2))
                {
                    var key = $"{aspectInfo2.Schema}.{aspectInfo2.ClassName}";
                    if (!aspectPropertiesByClass.ContainsKey(key))
                        aspectPropertiesByClass[key] = new List<string>();
                    aspectPropertiesByClass[key].Add(propName);
                }
                else if (suffixName != null && aspectPropertyMap.TryGetValue(suffixName, out var aspectInfo3))
                {
                    // e.g., BearingGroup_BuildOrderBeam → suffix "BuildOrderBeam" matches aspect property
                    var key = $"{aspectInfo3.Schema}.{aspectInfo3.ClassName}";
                    if (!aspectPropertiesByClass.ContainsKey(key))
                        aspectPropertiesByClass[key] = new List<string>();
                    aspectPropertiesByClass[key].Add(propName);
                }
                else if (propName.Contains('_') && !propertyKinds.ContainsKey(lookupName))
                {
                    // Underscore property whose parent isn't in element metadata — try deeper suffix matching
                    var matched = false;
                    foreach (var (aspectProp, aspInfo) in aspectPropertyMap)
                    {
                        if (propName.EndsWith("_" + aspectProp, StringComparison.OrdinalIgnoreCase) ||
                            propName.StartsWith(aspectProp + "_", StringComparison.OrdinalIgnoreCase) ||
                            propName.Equals(aspectProp, StringComparison.OrdinalIgnoreCase))
                        {
                            var key = $"{aspInfo.Schema}.{aspInfo.ClassName}";
                            if (!aspectPropertiesByClass.ContainsKey(key))
                                aspectPropertiesByClass[key] = new List<string>();
                            aspectPropertiesByClass[key].Add(propName);
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                        elementPropertyNames.Add(propName);
                }
                else
                {
                    elementPropertyNames.Add(propName);
                }
            }

            Console.WriteLine($"[INFO] Properties: {elementPropertyNames.Count} element-level, " +
                $"{aspectPropertiesByClass.Sum(kv => kv.Value.Count)} aspect-level " +
                $"({aspectPropertiesByClass.Count} aspect classes: {string.Join(", ", aspectPropertiesByClass.Keys)})");

            // Step 6e: Query element-level properties
            // Always ensure Origin is fetched for geometry staging, even if not in property mappings
            var geometryProps = new[] { "Origin" };
            foreach (var gp in geometryProps)
            {
                if (!elementPropertyNames.Contains(gp, StringComparer.OrdinalIgnoreCase) &&
                    !aspectPropertiesByClass.Values.Any(v => v.Contains(gp, StringComparer.OrdinalIgnoreCase)))
                    elementPropertyNames.Add(gp);
            }

            List<Dictionary<string, object?>> elements = new();

            if (elementPropertyNames.Count > 0)
            {
                var (elementQuery, elementOutputNames) = ECSqlQueryBuilder.BuildElementQuery(
                    selectedSchema, selectedClassName, elementPropertyNames, propertyKinds, primitiveTypes, maxElements);

                try
                {
                    elements = await apiClient.ExecuteECSqlAsync(elementQuery, ct, elementOutputNames);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Element query failed: {ex.Message}");
                    Console.WriteLine("[INFO] Identifying non-queryable properties...");

                    // Fallback: test each property individually
                    var workingProps = new List<string>();
                    foreach (var prop in elementPropertyNames)
                    {
                        var (testQuery, testNames) = ECSqlQueryBuilder.BuildElementQuery(
                            selectedSchema, selectedClassName, new List<string> { prop }, propertyKinds, primitiveTypes, 1);
                        try
                        {
                            await apiClient.ExecuteECSqlAsync(testQuery, ct, testNames);
                            workingProps.Add(prop);
                        }
                        catch
                        {
                            Console.WriteLine($"[WARN] Excluding non-queryable element property: {prop}");
                        }
                    }

                    if (workingProps.Count > 0)
                    {
                        var (finalQuery, finalNames) = ECSqlQueryBuilder.BuildElementQuery(
                            selectedSchema, selectedClassName, workingProps, propertyKinds, primitiveTypes, maxElements);
                        elements = await apiClient.ExecuteECSqlAsync(finalQuery, ct, finalNames);
                    }
                    else
                    {
                        var minQuery = $"SELECT e.ECInstanceId AS [ECInstanceId] FROM [{selectedSchema}].[{selectedClassName}] e LIMIT {maxElements}";
                        elements = await apiClient.ExecuteECSqlAsync(minQuery, ct, new[] { "ECInstanceId" });
                    }
                }
            }
            else
            {
                var idQuery = $"SELECT e.ECInstanceId AS [ECInstanceId] FROM [{selectedSchema}].[{selectedClassName}] e LIMIT {maxElements}";
                elements = await apiClient.ExecuteECSqlAsync(idQuery, ct, new[] { "ECInstanceId" });
            }

            Console.WriteLine($"[INFO] Retrieved {elements.Count} elements from iModel");

            // Step 6e-1.5: Combine expanded struct sub-properties back into JSON (Point3d → JSON)
            var structProps = elementPropertyNames.Where(p => IsPointStruct(p) || p.Equals("Placement", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var elem in elements)
            {
                foreach (var sp in structProps)
                {
                    var xKey = sp.Equals("Placement", StringComparison.OrdinalIgnoreCase) ? "Placement__X" : $"{sp}__X";
                    var yKey = sp.Equals("Placement", StringComparison.OrdinalIgnoreCase) ? "Placement__Y" : $"{sp}__Y";
                    var zKey = sp.Equals("Placement", StringComparison.OrdinalIgnoreCase) ? "Placement__Z" : $"{sp}__Z";
                    var x = elem.GetValueOrDefault(xKey)?.ToString();
                    var y = elem.GetValueOrDefault(yKey)?.ToString();
                    var z = elem.GetValueOrDefault(zKey)?.ToString();
                    if (x != null || y != null || z != null)
                        elem[sp] = $"{{\"x\":{x ?? "0"},\"y\":{y ?? "0"},\"z\":{z ?? "0"}}}";
                    elem.Remove(xKey); elem.Remove(yKey); elem.Remove(zKey);
                }
            }

            static bool IsPointStruct(string name) => name.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("BBoxLow", StringComparison.OrdinalIgnoreCase)
                || name.Equals("BBoxHigh", StringComparison.OrdinalIgnoreCase);

            // Step 6e-2: Resolve Parent navigation property IDs to parent element details
            if (elementPropertyNames.Any(p => p.Equals("Parent", StringComparison.OrdinalIgnoreCase)) && elements.Count > 0)
            {
                // Collect unique non-null Parent IDs
                var parentIds = elements
                    .Select(e => e.GetValueOrDefault("Parent")?.ToString())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (parentIds.Count > 0)
                {
                    Console.WriteLine($"[INFO] Resolving {parentIds.Count} unique Parent IDs...");
                    // Query parent elements to get their UserLabel/CodeValue/ClassName
                    var inList = string.Join(",", parentIds);
                    var parentQuery = $"SELECT ECInstanceId, UserLabel, CodeValue, ec_classname(ECClassId) AS ClassName FROM bis.Element WHERE ECInstanceId IN ({inList})";
                    var parentResults = await apiClient.ExecuteECSqlAsync(parentQuery, ct, new[] { "ECInstanceId", "UserLabel", "CodeValue", "ClassName" });

                    // Build lookup: parentId → resolved name (UserLabel → CodeValue → ClassName → raw ID)
                    var parentLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pr in parentResults)
                    {
                        var pid = pr.GetValueOrDefault("ECInstanceId")?.ToString();
                        var label = pr.GetValueOrDefault("UserLabel")?.ToString();
                        var code = pr.GetValueOrDefault("CodeValue")?.ToString();
                        var className = pr.GetValueOrDefault("ClassName")?.ToString();
                        // Strip schema prefix (e.g., "BridgeStructuralPhysical:GenericSubstructureElement" → "GenericSubstructureElement")
                        if (!string.IsNullOrEmpty(className) && className.Contains(':'))
                            className = className.Split(':').Last();
                        var resolvedName = !string.IsNullOrEmpty(label) ? label
                            : !string.IsNullOrEmpty(code) ? code
                            : !string.IsNullOrEmpty(className) ? className
                            : pid ?? "";
                        if (!string.IsNullOrEmpty(pid))
                            parentLookup[pid] = resolvedName;
                    }

                    Console.WriteLine($"[INFO] Resolved {parentLookup.Count} parent elements");
                    // Replace Parent ID with resolved name in each element
                    foreach (var elem in elements)
                    {
                        var parentId = elem.GetValueOrDefault("Parent")?.ToString();
                        if (!string.IsNullOrEmpty(parentId) && parentLookup.TryGetValue(parentId, out var parentName))
                            elem["Parent"] = parentName;
                    }
                }
            }

            // Step 6f: Query aspect properties and merge into element rows
            if (aspectPropertiesByClass.Count > 0 && elements.Count > 0)
            {
                var elementsByInstanceId = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
                foreach (var elem in elements)
                {
                    var id = elem.GetValueOrDefault("ECInstanceId")?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        elementsByInstanceId[id] = elem;
                }

                foreach (var (aspectFullName, aspectProps) in aspectPropertiesByClass)
                {
                    var parts = aspectFullName.Split('.');
                    var aspectSchema = parts[0];
                    var aspectClassName = parts[1];

                    var (aspectQuery, aspectOutputNames) = ECSqlQueryBuilder.BuildAspectQuery(
                        aspectSchema, aspectClassName, aspectProps, aspectPropertyKinds, maxElements * 2,
                        selectedSchema, selectedClassName);

                    try
                    {
                        var aspectRows = await apiClient.ExecuteECSqlAsync(aspectQuery, ct, aspectOutputNames);

                        foreach (var aspectRow in aspectRows)
                        {
                            var elementId = aspectRow.GetValueOrDefault("elementId")?.ToString();
                            if (!string.IsNullOrEmpty(elementId) && elementsByInstanceId.TryGetValue(elementId, out var elementRow))
                            {
                                foreach (var prop in aspectProps)
                                {
                                    if (aspectRow.TryGetValue(prop, out var value))
                                        elementRow[prop] = value;
                                }
                            }
                        }

                        Console.WriteLine($"[INFO] Merged {aspectRows.Count} rows from aspect {aspectFullName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARN] Aspect query failed for {aspectFullName}: {ex.Message}");
                        // Try each property individually
                        foreach (var prop in aspectProps)
                        {
                            try
                            {
                                var (singleQuery, singleNames) = ECSqlQueryBuilder.BuildAspectQuery(
                                    aspectSchema, aspectClassName, new List<string> { prop }, aspectPropertyKinds, maxElements * 2,
                                    selectedSchema, selectedClassName);
                                var singleRows = await apiClient.ExecuteECSqlAsync(singleQuery, ct, singleNames);

                                foreach (var row in singleRows)
                                {
                                    var elementId = row.GetValueOrDefault("elementId")?.ToString();
                                    if (!string.IsNullOrEmpty(elementId) && elementsByInstanceId.TryGetValue(elementId, out var elementRow))
                                    {
                                        if (row.TryGetValue(prop, out var value))
                                            elementRow[prop] = value;
                                    }
                                }
                            }
                            catch
                            {
                                Console.WriteLine($"[WARN] Excluding non-queryable aspect property: {prop}");
                            }
                        }
                    }
                }
            }

            // ─── Step 7: Stage the data ───
            Console.WriteLine("\n[INFO] Staging data to Staging DB...");

            var stagedFeatureIds = new List<Guid>();
            var bucketId = featureTypeSubmission.BucketId;

            foreach (var element in elements)
            {
                var featureId = await coreDb.InsertStagingFeatureAsync(iTwinId, featureTypeSubmission.Id, bucketId);
                stagedFeatureIds.Add(featureId);

                foreach (var mapping in mappings)
                {
                    var sourcePropName = propLookup.GetValueOrDefault(mapping.DataSegmentPropertyId, "");
                    string? sourceValue = null;
                    if (!string.IsNullOrEmpty(sourcePropName) && element.TryGetValue(sourcePropName, out var val))
                        sourceValue = val?.ToString();

                    // Apply transformation pipeline (same as IModelStagingService)
                    var finalValue = sourceValue;

                    // Step 2: Formula (placeholder)
                    // Step 3: DefaultValue
                    if (string.IsNullOrEmpty(finalValue) && !string.IsNullOrEmpty(mapping.DefaultValue))
                        finalValue = mapping.DefaultValue;

                    // Step 4: UseCurrentDate
                    if (string.IsNullOrEmpty(finalValue) && mapping.UseCurrentDate)
                        finalValue = DateTime.UtcNow.ToString("O");

                    await coreDb.InsertStagingFeaturePropertyAsync(iTwinId, featureId, mapping.TargetPropertyId, bucketId, sourceValue, finalValue);
                }

                // ─── Insert geometry using iModel coordinate API ───
                var originJson = element.GetValueOrDefault("Origin")?.ToString();
                if (!string.IsNullOrEmpty(originJson))
                {
                    try
                    {
                        using var origDoc = JsonDocument.Parse(originJson);
                        var ox = origDoc.RootElement.TryGetProperty("x", out var ex1) ? ex1.GetDouble() : 0;
                        var oy = origDoc.RootElement.TryGetProperty("y", out var ey1) ? ey1.GetDouble() : 0;
                        var oz = origDoc.RootElement.TryGetProperty("z", out var ez1) ? ez1.GetDouble() : 0;

                        var wgs84 = await apiClient.ConvertToWgs84Async(ox, oy, oz, ct);
                        if (wgs84 != null)
                        {
                            var wkt = $"POINT({wgs84.Value.Longitude.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)} {wgs84.Value.Latitude.ToString("F8", System.Globalization.CultureInfo.InvariantCulture)} {wgs84.Value.Elevation.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)})";
                            await coreDb.InsertStagingFeatureGeometryAsync(iTwinId, featureId, bucketId, "POINT", wkt);
                            Console.WriteLine($"[INFO] ✓ POINT geometry inserted for element {element.GetValueOrDefault("ECInstanceId")}");
                        }
                        else
                        {
                            Console.WriteLine($"[WARN] Coordinate conversion returned null for element {element.GetValueOrDefault("ECInstanceId")} — geometry skipped");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WARN] Geometry insert failed for element {element.GetValueOrDefault("ECInstanceId")}: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[WARN] No Origin data for element {element.GetValueOrDefault("ECInstanceId")} — geometry skipped");
                }
            } // end foreach element

            Console.WriteLine($"\n[INFO] ✓ Submission created successfully!");
            Console.WriteLine($"       Feature Type Submission: {featureTypeSubmission.Id}");
            Console.WriteLine($"       Elements staged: {stagedFeatureIds.Count}");

            // ─── Step 8: Display staged data ───
            Console.WriteLine("\n╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║              Staged Data Summary                     ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝\n");

            var stagedFeatures = await coreDb.GetStagedFeaturesAsync(iTwinId, featureTypeSubmission.Id);
            Console.WriteLine($"  Total Staged Features: {stagedFeatures.Count}");
            Console.WriteLine();

            // Show first 5 features with their properties
            var displayCount = Math.Min(5, stagedFeatures.Count);
            for (int i = 0; i < displayCount; i++)
            {
                var feature = stagedFeatures[i];
                Console.WriteLine($"  Feature #{i + 1} | ID: {feature.Id} | Status: {feature.Status} | Recommendation: {feature.SystemRecommendation}");

                var props = await coreDb.GetStagedFeaturePropertiesAsync(iTwinId, feature.Id);
                foreach (var prop in props)
                {
                    // Resolve name: ARS resolved name → source property name fallback → GUID
                    var targetName = targetPropertyNames.TryGetValue(prop.TargetPropertyId, out var resolvedTarget) ? resolvedTarget : null;
                    targetName ??= mappings.Where(m => m.TargetPropertyId == prop.TargetPropertyId)
                            .Select(m => propLookup.GetValueOrDefault(m.DataSegmentPropertyId, ""))
                            .FirstOrDefault(n => !string.IsNullOrEmpty(n))
                        ?? prop.TargetPropertyId.ToString();
                    var srcDisplay = prop.SourceValue ?? "(null)";
                    var finalDisplay = prop.FinalValue ?? "(null)";
                    if (srcDisplay.Length > 40) srcDisplay = srcDisplay[..40] + "...";
                    if (finalDisplay.Length > 40) finalDisplay = finalDisplay[..40] + "...";
                    Console.WriteLine($"    {targetName,-30} | Source: {srcDisplay,-25} | Final: {finalDisplay}");
                }
                Console.WriteLine();
            }

            if (stagedFeatures.Count > displayCount)
                Console.WriteLine($"  ... and {stagedFeatures.Count - displayCount} more features.");

            Console.WriteLine($"\n[INFO] Done with {selectedClassName}!");
        } // end for loop over all classes

        Console.WriteLine("\n[INFO] ✓ All classes processed successfully.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[FATAL] Unhandled exception: {ex}");
        context.ExitCode = 1;
    }
});

return await rootCommand.InvokeAsync(args);
