using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using iModelImportConsole.Auth;
using iModelImportConsole.Config;
using iModelImportConsole.Services;

//  Command-line options 
var envOption = new Option<string>("--env", () => "qa", "Environment: dev or qa");
var limitOption = new Option<int>("--limit", () => 1000, "Max elements to load per ECClass");

var rootCommand = new RootCommand("iModel Import Console  Stage iModel data via RPC")
{
    envOption, limitOption
};

rootCommand.SetHandler(async (context) =>
{
    var env = context.ParseResult.GetValueForOption(envOption)!;
    var limit = context.ParseResult.GetValueForOption(limitOption);
    var ct = context.GetCancellationToken();

    //  Load config ─
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

    var defaults = config.GetSection("Defaults");
    var iTwinId = defaults["ITwinId"]!;
    var iModelId = defaults["IModelId"]!;
    var changesetId = defaults["ChangesetId"]!;

    Console.WriteLine("");
    Console.WriteLine("       iModel Import Console  RPC Staging           ");
    Console.WriteLine("");
    Console.WriteLine($"  Environment : {env.ToUpper()}");
    Console.WriteLine($"  Base URL    : {envConfig.ApiBaseUrl}");
    Console.WriteLine($"  iTwinId     : {iTwinId}");
    Console.WriteLine($"  iModelId    : {iModelId}");
    Console.WriteLine($"  ChangesetId : {changesetId}");
    Console.WriteLine($"  Limit       : {limit} element(s) per class");
    Console.WriteLine();

    try
    {
        //  0. Authenticate 
        Console.WriteLine("[INFO] Authenticating...");
        var (rawToken, expiresIn) = await TokenManager.AcquireClientCredentialsTokenAsync(envConfig, ct);
        Console.WriteLine($"[INFO] Token acquired (valid {expiresIn / 60} min)");

        //  0b. Fetch changeset index 
        // The RPC backend needs changeset.index to locate the nearest checkpoint.
        // Without it the backend cannot determine ordering and returns "iModel not found".
        int changesetIndex = 0;
        try
        {
            using var csHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            csHttp.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawToken);
            csHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.bentley.itwin-platform.v2+json");
            var csUrl = $"{envConfig.ApiBaseUrl}/imodels/{iModelId}/changesets/{changesetId}";
            var csResp = await csHttp.GetAsync(csUrl, ct);
            if (csResp.IsSuccessStatusCode)
            {
                var csJson = await csResp.Content.ReadAsStringAsync(ct);
                using var csDoc = JsonDocument.Parse(csJson);
                var csRoot = csDoc.RootElement;
                // response: { "changeset": { "index": 14, ... } }
                if (csRoot.TryGetProperty("changeset", out var csEl) &&
                    csEl.TryGetProperty("index", out var idxEl))
                    changesetIndex = idxEl.GetInt32();
            }
            Console.WriteLine($"[INFO] Changeset index: {changesetIndex}");
        }
        catch (Exception ex) { Console.WriteLine($"[WARN] Could not fetch changeset index: {ex.Message}"); }

        //  1. Probe RPC connection variants 
        // Try mode 1/2 × full-changeset/empty-latest to find what the backend accepts.
        Console.WriteLine();
        Console.WriteLine("[PROBE] Finding a working RPC connection variant...");
        Console.WriteLine($"  {"Variant",-46} {"Result",-10}");
        Console.WriteLine($"  {new string('-', 46)} {new string('-', 10)}");

        var probeVariants = new (int Mode, string UrlCs, string Desc)[]
        {
            (1, changesetId, $"mode=1  cs={changesetId[..Math.Min(8,changesetId.Length)]} (full hash)"),
            (1, "",          "mode=1  cs=latest (no changeset segment)      "),
            (2, changesetId, $"mode=2  cs={changesetId[..Math.Min(8,changesetId.Length)]} (full hash)"),
            (2, "",          "mode=2  cs=latest (no changeset segment)      "),
        };

        int workingMode = -1;
        string workingUrlCs = changesetId;

        foreach (var (vMode, vUrlCs, vDesc) in probeVariants)
        {
            Console.Write($"  {vDesc,-46} ");
            using var probe = new RpcIModelService(
                new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                envConfig.ApiBaseUrl, iTwinId, iModelId, changesetId,
                mode: vMode, changesetIndex: changesetIndex);
            try
            {
                var r = await probe.QueryRowsAsync(
                    "SELECT e.ECInstanceId FROM BisCore.Element e LIMIT 1",
                    rawToken, ct, urlChangeset: vUrlCs);

                if (r.IsSuccess)
                {
                    Console.WriteLine(" OK");
                    workingMode = vMode;
                    workingUrlCs = vUrlCs;
                    break;
                }
                Console.WriteLine($" RPC status={r.Status} {r.ErrorMessage}");
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                var shortMsg = ex.Message.Length > 90 ? ex.Message[..90] + "" : ex.Message;
                Console.WriteLine($" {shortMsg}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" {ex.GetType().Name}: {ex.Message[..Math.Min(70, ex.Message.Length)]}");
            }
        }

        Console.WriteLine();

        if (workingMode == -1)
        {
            Console.WriteLine("[ERROR] All 4 RPC connection variants failed.");
            Console.WriteLine("        Possible causes:");
            Console.WriteLine("          1. Service account not added as iTwin participant (RBAC)");
            Console.WriteLine("          2. No checkpoint available for this iModel/changeset");
            Console.WriteLine("          3. Token missing required scope (itwin-platform)");
            context.ExitCode = 1;
            return;
        }

        var csDisplay = string.IsNullOrEmpty(workingUrlCs) ? "(latest)" : workingUrlCs[..Math.Min(12, workingUrlCs.Length)] + "";
        Console.WriteLine($"[INFO] Using mode={workingMode}  urlChangeset={csDisplay}  x-protocol-version:2");

        using var rpcSvc = new RpcIModelService(
            new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) },
            envConfig.ApiBaseUrl, iTwinId, iModelId, changesetId,
            mode: workingMode, changesetIndex: changesetIndex);

        //  2. Load Core DB mapped ECClasses 
        Console.WriteLine($"\n[STEP 1] Loading mapped ECClasses from Core DB...");
        var dbConfig = new DatabaseConfig();
        config.GetSection("Database").Bind(dbConfig);
        var coreDb = new CoreDbService(dbConfig);
        var allSegments = await coreDb.GetAllDataSegmentsAsync(Guid.Parse(iTwinId));

        var allMappedClassNames = allSegments
            .Where(s => !string.IsNullOrEmpty(s.Metadata))
            .Select(s =>
            {
                try
                {
                    using var metaDoc = JsonDocument.Parse(s.Metadata!);
                    return metaDoc.RootElement.TryGetProperty("className", out var cn)
                        ? cn.GetString() : null;
                }
                catch { return null; }
            })
            .Where(cn => !string.IsNullOrEmpty(cn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;

        if (allMappedClassNames.Count == 0)
        {
            Console.WriteLine("[WARN] No mapped ECClasses found in Core DB for this iTwin  nothing to process.");
            context.ExitCode = 1;
            return;
        }

        Console.WriteLine($"[INFO] Found {allMappedClassNames.Count} mapped ECClass(es):");
        foreach (var cn in allMappedClassNames)
            Console.WriteLine($"    \u2022 {cn}");
        Console.WriteLine();

        //  Load staging context per mapped segment via RPC queryRows 
        Console.WriteLine("[STEP 1b] Loading staging context + property metadata via RPC queryRows...");
        var iTwinGuid = Guid.Parse(iTwinId);

        var stagingContexts = new Dictionary<string, (
            DataSegmentInfo Segment,
            FeatureTypeSubmissionInfo? Submission,
            List<PropertyMappingInfo> Mappings,
            Dictionary<Guid, string> PropLookup,
            List<string> ElementPropNames,
            Dictionary<string, List<string>> AspectPropertiesByClass,
            Dictionary<string, int> AspectPropertyKinds,
            string Schema)>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in allSegments)
        {
            string? segCn = null;
            if (!string.IsNullOrEmpty(segment.Metadata))
            {
                try
                {
                    using var m = JsonDocument.Parse(segment.Metadata);
                    segCn = m.RootElement.TryGetProperty("className", out var cv) ? cv.GetString() : null;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(segCn)
                || !allMappedClassNames.Any(cn => string.Equals(cn, segCn, StringComparison.OrdinalIgnoreCase)))
                continue;

            var sub = await coreDb.GetFeatureTypeSubmissionAsync(iTwinGuid, segment.FeatureTypeId);
            var segProps = await coreDb.GetSegmentPropertiesAsync(iTwinGuid, segment.Id);
            var maps = await coreDb.GetPropertyMappingsAsync(iTwinGuid, segment.Id);
            var lookup = segProps.ToDictionary(p => p.Id, p => p.Name);
            var allPropNames = segProps.Select(p => p.Name).ToList();

            // Resolve schema name via RPC
            string schema = "BisCore";
            var schemaResult = await rpcSvc.QueryRowsAsync(
                $"SELECT DISTINCT ec_classname(e.ECClassId,'s') AS SchemaName " +
                $"FROM BisCore.GeometricElement3d e WHERE ec_classname(e.ECClassId,'c') = '{segCn}' LIMIT 1",
                rawToken, ct, urlChangeset: workingUrlCs);
            if (schemaResult.IsSuccess && schemaResult.Rows.Count > 0)
                schema = schemaResult.Rows[0].GetValueOrDefault("SchemaName")?.ToString() ?? "BisCore";

            // Element-level property kinds via RPC
            var propertyKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var metaResult = await rpcSvc.QueryRowsAsync(
                ECSqlQueryBuilder.BuildPropertyMetadataQuery(schema, segCn),
                rawToken, ct, urlChangeset: workingUrlCs);
            if (metaResult.IsSuccess)
                foreach (var row in metaResult.Rows)
                {
                    var pn = row.GetValueOrDefault("PropertyName")?.ToString();
                    if (!string.IsNullOrEmpty(pn) && int.TryParse(row.GetValueOrDefault("PropertyKind")?.ToString(), out var kind))
                        propertyKinds[pn] = kind;
                }

            // Aspect class discovery via RPC
            var aspectClasses = new List<(string Schema, string ClassName, string ClassId)>();
            var aspectDiscResult = await rpcSvc.QueryRowsAsync(
                ECSqlQueryBuilder.BuildAspectDiscoveryQuery(schema, segCn),
                rawToken, ct, urlChangeset: workingUrlCs);
            if (aspectDiscResult.IsSuccess)
                foreach (var row in aspectDiscResult.Rows)
                {
                    var aSch = row.GetValueOrDefault("AspectSchemaName")?.ToString();
                    var aCls = row.GetValueOrDefault("AspectClassName")?.ToString();
                    var aCid = row.GetValueOrDefault("AspectClassId")?.ToString();
                    if (!string.IsNullOrEmpty(aSch) && !string.IsNullOrEmpty(aCls) && !string.IsNullOrEmpty(aCid))
                        aspectClasses.Add((aSch, aCls, aCid));
                }

            // Aspect property kinds via RPC
            var aspectPropertyMap = new Dictionary<string, (string Schema, string ClassName)>(StringComparer.OrdinalIgnoreCase);
            var aspectPropertyKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (aSchema, aCls, aCid) in aspectClasses)
            {
                var apResult = await rpcSvc.QueryRowsAsync(
                    ECSqlQueryBuilder.BuildAspectPropertyMetadataQueryByClassId(aCid),
                    rawToken, ct, urlChangeset: workingUrlCs);
                if (!apResult.IsSuccess) continue;
                foreach (var row in apResult.Rows)
                {
                    var pn = row.GetValueOrDefault("PropertyName")?.ToString();
                    if (!string.IsNullOrEmpty(pn) && int.TryParse(row.GetValueOrDefault("PropertyKind")?.ToString(), out var kind))
                    { aspectPropertyMap[pn] = (aSchema, aCls); aspectPropertyKinds[pn] = kind; }
                }
            }

            // Separate mapped prop names: element-level vs aspect-level
            var elementPropNames = new List<string>();
            var aspectPropertiesByClass = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var propName in allPropNames)
            {
                var lookupName = propName.Contains('_') ? propName.Split('_')[0] : propName;
                var suffixName = propName.Contains('_') ? propName.Split('_', 2)[1] : null;
                if (aspectPropertyMap.TryGetValue(lookupName, out var ai))
                { var k = $"{ai.Schema}.{ai.ClassName}"; if (!aspectPropertiesByClass.ContainsKey(k)) aspectPropertiesByClass[k] = new(); aspectPropertiesByClass[k].Add(propName); }
                else if (propName.Contains('_') && aspectPropertyMap.TryGetValue(propName, out var ai2))
                { var k = $"{ai2.Schema}.{ai2.ClassName}"; if (!aspectPropertiesByClass.ContainsKey(k)) aspectPropertiesByClass[k] = new(); aspectPropertiesByClass[k].Add(propName); }
                else if (suffixName != null && aspectPropertyMap.TryGetValue(suffixName, out var ai3))
                { var k = $"{ai3.Schema}.{ai3.ClassName}"; if (!aspectPropertiesByClass.ContainsKey(k)) aspectPropertiesByClass[k] = new(); aspectPropertiesByClass[k].Add(propName); }
                else if (propName.Contains('_') && !propertyKinds.ContainsKey(lookupName))
                {
                    bool matched = false;
                    foreach (var (ap, aInfo) in aspectPropertyMap)
                    {
                        if (propName.EndsWith("_" + ap, StringComparison.OrdinalIgnoreCase) ||
                            propName.StartsWith(ap + "_", StringComparison.OrdinalIgnoreCase) ||
                            propName.Equals(ap, StringComparison.OrdinalIgnoreCase))
                        { var k = $"{aInfo.Schema}.{aInfo.ClassName}"; if (!aspectPropertiesByClass.ContainsKey(k)) aspectPropertiesByClass[k] = new(); aspectPropertiesByClass[k].Add(propName); matched = true; break; }
                    }
                    if (!matched) elementPropNames.Add(propName);
                }
                else
                    elementPropNames.Add(propName);
            }

            stagingContexts[segCn] = (segment, sub, maps, lookup, elementPropNames, aspectPropertiesByClass, aspectPropertyKinds, schema);
            Console.WriteLine($"  [{segCn}] {maps.Count} mapping(s)  " +
                $"{elementPropNames.Count} element-props  " +
                $"{aspectPropertiesByClass.Sum(kv => kv.Value.Count)} aspect-props  " +
                $"sub={sub?.Id.ToString()[..8] ?? "MISSING"}\u2026  bucket={sub?.BucketId}");
        }
        Console.WriteLine();

        // WGS84 coordinate conversion  uses the imodel-query coordinate REST API.
        // NOTE: IModelApiClient is preserved for reference; only ConvertBatchToWgs84Async
        // is called here. No ECSQL queries are made through this client.
        using var coordConverter = new IModelApiClient(
            envConfig, new TokenManager(rawToken), iModelId, iTwinId, changesetId);

        int totalQueried = 0, stagedCount = 0, geomStagedCount = 0;
        var geomSourceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["geom"] = 0,
            ["brep+geom"] = 0,
            ["brep\u2192bbox"] = 0,
            ["brep\u2192point"] = 0,
            ["bbox_fallback"] = 0,
            ["skipped"] = 0,
        };
        var invCulture = System.Globalization.CultureInfo.InvariantCulture;

        //  Per-class processing loop 
        foreach (var (className, ctx) in stagingContexts)
        {
            Console.WriteLine($"\n");
            Console.WriteLine($"  {ctx.Schema}.{className}");
            Console.WriteLine($"  ARS: {ctx.Segment.DisplayLabel}    {ctx.Mappings.Count} mapped properties");
            Console.WriteLine($"");

            //  STEP 2: queryRows  element IDs 
            Console.WriteLine($"[STEP 2] queryRows  element IDs for '{className}' (limit {limit})");
            var idSql = $"SELECT e.ECInstanceId FROM BisCore.GeometricElement3d e " +
                           $"WHERE ec_classname(e.ECClassId,'c') = '{className}' LIMIT {limit}";
            var idResult = await rpcSvc.QueryRowsAsync(idSql, rawToken, ct, urlChangeset: workingUrlCs);

            if (!idResult.IsSuccess)
            { Console.WriteLine($"  \u2717 queryRows failed \u2014 status={idResult.Status} {idResult.ErrorMessage}"); continue; }

            var elementIds = idResult.Rows
                .Select(r => r.GetValueOrDefault("ECInstanceId")?.ToString())
                .Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList();
            Console.WriteLine($"  \u2713 {elementIds.Count} element(s)");
            totalQueried += elementIds.Count;
            if (elementIds.Count == 0) continue;

            //  STEP 3: loadElementProps  element-level props + geometry 
            Console.WriteLine($"[STEP 3] loadElementProps (wantGeometry:true) \u2014 {elementIds.Count} element(s)");
            Console.WriteLine($"  {"\u2500\u2500 Element ID \u2500\u2500",20}  {"\u2500\u2500\u2500 Label \u2500\u2500\u2500",32}  {"\u2500\u2500 Resolution path [stream types] \u2500\u2500",34}  {"Pts",3}  ARS");
            var elemResults = new Dictionary<string, RpcIModelElement>(StringComparer.OrdinalIgnoreCase);

            foreach (var elementId in elementIds)
            {
                var elem = await rpcSvc.LoadElementGeometryAsync(
                    elementId, rawToken, ct,
                    urlChangeset: workingUrlCs,
                    extractProps: ctx.ElementPropNames);

                if (elem is null) { Console.WriteLine($"  [{elementId}] \u2192 null"); continue; }

                elemResults[elementId] = elem;
                var src = elem.GeometrySource;
                var srcKey = src switch
                {
                    "geom" => "geom",
                    "brep+geom" => "brep+geom",
                    "brep\u2192bbox" => "brep\u2192bbox",
                    "brep\u2192point" => "brep\u2192point",
                    _ => elem.IModelCoordinates.Count == 0 ? "skipped" : "bbox_fallback"
                };
                if (geomSourceCounts.ContainsKey(srcKey)) geomSourceCounts[srcKey]++;
                else geomSourceCounts[srcKey] = 1;

                var streamTypes = elem.GeometryTypes.Count > 0
                    ? $" [{string.Join(",", elem.GeometryTypes.Distinct())}]"
                    : "";
                var geomLabel = $"{elem.GeometrySource}{streamTypes}";
                var isLinear = ElementGeometryExtractor.IsLinear(elem.GeometryTypes, elem.GeometrySource);
                var arsType = elem.IModelCoordinates.Count == 0 ? "SKIPPED"
                              : elem.IModelCoordinates.Count == 1 ? "POINT"
                              : isLinear ? "LINE"
                              : "POLYGON";
                Console.WriteLine($"  {elementId,-20}  {$"\"{elem.UserLabel ?? "\u2014"}\"",-32}  {geomLabel,-34}  {elem.IModelCoordinates.Count,3}  \u2192 {arsType}");
            }
            Console.WriteLine($"  \u2192 {elemResults.Count}/{elementIds.Count} loaded");

            //  STEP 4: queryRows  aspect-level properties 
            var aspectData = new Dictionary<string, Dictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);

            if (ctx.AspectPropertiesByClass.Count > 0)
            {
                Console.WriteLine($"[STEP 4] queryRows aspect props ({ctx.AspectPropertiesByClass.Count} aspect class(es))");
                foreach (var (aspectFull, aspectPropNames) in ctx.AspectPropertiesByClass)
                {
                    var aParts = aspectFull.Split('.');
                    var aSchema = aParts[0]; var aCls = aParts[1];

                    var (aspectSql, _) = ECSqlQueryBuilder.BuildAspectQuery(
                        aSchema, aCls, aspectPropNames, ctx.AspectPropertyKinds,
                        limit * 2, ctx.Schema, className);

                    var aResult = await rpcSvc.QueryRowsAsync(aspectSql, rawToken, ct, urlChangeset: workingUrlCs);
                    if (!aResult.IsSuccess)
                    { Console.WriteLine($"  [{aspectFull}] \u2717 {aResult.ErrorMessage}"); continue; }

                    foreach (var row in aResult.Rows)
                    {
                        var eid = row.GetValueOrDefault("elementId")?.ToString();
                        if (string.IsNullOrEmpty(eid)) continue;
                        if (!aspectData.TryGetValue(eid, out var pd))
                        { pd = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); aspectData[eid] = pd; }
                        foreach (var pn in aspectPropNames)
                            if (row.TryGetValue(pn, out var v)) pd[pn] = v?.ToString();
                    }
                    Console.WriteLine($"  [{aspectFull}] \u2713 {aResult.Rows.Count} row(s)");
                }
            }
            else
                Console.WriteLine($"[STEP 4] No aspect classes for '{className}' \u2014 skipped");

            //  STEP 5: Stage properties + geometry to DB 
            Console.WriteLine($"[STEP 5] Staging {elemResults.Count} feature(s)...");

            if (ctx.Submission == null)
            { Console.WriteLine($"  [SKIP] No feature type submission \u2014 nothing staged"); continue; }

            foreach (var (elementId, elem) in elemResults)
            {
                var featureId = await coreDb.InsertStagingFeatureAsync(
                    iTwinGuid, ctx.Submission.Id, ctx.Submission.BucketId);
                stagedCount++;

                aspectData.TryGetValue(elementId, out var aspectProps);

                // Stage mapped properties: element-level (loadElementProps) + aspect-level (queryRows)
                foreach (var mapping in ctx.Mappings)
                {
                    var srcName = ctx.PropLookup.GetValueOrDefault(mapping.DataSegmentPropertyId, "");
                    string? srcVal = null;
                    if (!string.IsNullOrEmpty(srcName))
                    {
                        if (!elem.Properties.TryGetValue(srcName, out srcVal) || srcVal == null)
                            aspectProps?.TryGetValue(srcName, out srcVal);
                    }

                    var finalVal = srcVal;
                    if (string.IsNullOrEmpty(finalVal) && !string.IsNullOrEmpty(mapping.DefaultValue))
                        finalVal = mapping.DefaultValue;
                    if (string.IsNullOrEmpty(finalVal) && mapping.UseCurrentDate)
                        finalVal = DateTime.UtcNow.ToString("O");

                    await coreDb.InsertStagingFeaturePropertyAsync(
                        iTwinGuid, featureId, mapping.TargetPropertyId,
                        ctx.Submission.BucketId, srcVal, finalVal);
                }

                // Stage geometry: batch WGS84 conversion  WKT
                if (elem.IModelCoordinates.Count > 0)
                {
                    var iModelPts = elem.IModelCoordinates.Select(c => (X: c[0], Y: c[1], Z: 0.0)).ToList();
                    var wgs84Pts = await coordConverter.ConvertBatchToWgs84Async(iModelPts, ct);
                    var validPts = wgs84Pts.Where(p => p.HasValue).Select(p => p!.Value).ToList();

                    if (validPts.Count > 0)
                    {
                        string wkt, geomType;
                        bool isLinear = ElementGeometryExtractor.IsLinear(elem.GeometryTypes, elem.GeometrySource);

                        if (validPts.Count == 1)
                        {
                            wkt = $"POINT({validPts[0].Longitude.ToString("F8", invCulture)} {validPts[0].Latitude.ToString("F8", invCulture)})";
                            geomType = "POINT";
                        }
                        else if (isLinear)
                        {
                            var coords2 = validPts
                                .Select(p => $"{p.Longitude.ToString("F8", invCulture)} {p.Latitude.ToString("F8", invCulture)}");
                            wkt = $"LINESTRING({string.Join(", ", coords2)})";
                            geomType = "LINE";
                        }
                        else
                        {
                            var ring = validPts
                                .Select(p => $"{p.Longitude.ToString("F8", invCulture)} {p.Latitude.ToString("F8", invCulture)}")
                                .ToList();
                            ring.Add(ring[0]);  // close the ring
                            wkt = $"POLYGON(({string.Join(", ", ring)}))";
                            geomType = "POLYGON";
                        }

                        await coreDb.InsertStagingFeatureGeometryAsync(
                            iTwinGuid, featureId, ctx.Submission.BucketId, geomType, wkt);
                        geomStagedCount++;
                        Console.WriteLine($"  [{elementId}] staged id={featureId.ToString()[..8]}\u2026  geo={geomType} ({validPts.Count} WGS84 pts)");
                    }
                    else
                        Console.WriteLine($"  [{elementId}] staged id={featureId.ToString()[..8]}\u2026  geo=SKIPPED (no GCS)");
                }
                else
                    Console.WriteLine($"  [{elementId}] staged id={featureId.ToString()[..8]}\u2026  geo=SKIPPED (no coords)");
            }
            Console.WriteLine($"  \u2713 {elemResults.Count} feature(s) staged for '{className}'");
        }

        //  Summary 
        Console.WriteLine("\n");
        Console.WriteLine("                     Summary                        ");
        Console.WriteLine("");
        Console.WriteLine($"  RPC mode              : {workingMode}");
        Console.WriteLine($"  URL changeset         : {(string.IsNullOrEmpty(workingUrlCs) ? "(latest)" : workingUrlCs)}");
        Console.WriteLine($"  Changeset index       : {changesetIndex}");
        Console.WriteLine($"  Mapped classes        : {allMappedClassNames.Count}");
        Console.WriteLine($"  Elements queried      : {totalQueried}");
        Console.WriteLine($"  Geometry breakdown    :");
        Console.WriteLine($"    geom (stream)       : {geomSourceCounts["geom"]}");
        Console.WriteLine($"    brep+geom           : {geomSourceCounts["brep+geom"]}");
        Console.WriteLine($"    brep\u2192bbox          : {geomSourceCounts["brep\u2192bbox"]}");
        Console.WriteLine($"    brep\u2192point         : {geomSourceCounts["brep\u2192point"]}");
        Console.WriteLine($"    bbox_fallback       : {geomSourceCounts["bbox_fallback"]}");
        Console.WriteLine($"    skipped (no data)   : {geomSourceCounts["skipped"]}");
        Console.WriteLine($"  Features staged to DB : {stagedCount}");
        Console.WriteLine($"  Geometries staged     : {geomStagedCount}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[FATAL] Unhandled exception: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        context.ExitCode = 1;
    }
});

return await rootCommand.InvokeAsync(args);