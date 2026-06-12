using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace iModelImportConsole.Services;

// ─────────────────────────────────────────────────────────────────────────────
// RpcIModelElement — output model
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Result produced by <see cref="RpcIModelService"/> for a single iModel element:
/// metadata + its 2D footprint in iModel spatial coordinates (not yet georeferenced).
/// </summary>
public sealed class RpcIModelElement
{
  /// <summary>Hex element ID, e.g. "0x1a3f".</summary>
  public string ElementId { get; init; } = "";

  public string? UserLabel { get; init; }

  /// <summary>
  /// How the 2D footprint was obtained:
  /// <list type="bullet">
  ///   <item><c>"geom"</c>        — decoded directly from analytic geometry stream entries (lineString / loop / arc / box / solidPrimitive / polyface).</item>
  ///   <item><c>"brep+geom"</c>  — BRep solid present, but companion 2D geometry (lineString/loop/etc.) was also in the stream and used as footprint.</item>
  ///   <item><c>"brep→bbox"</c>  — pure BRep, no companion geometry; oriented POLYGON derived from placement origin + bbox extents.</item>
  ///   <item><c>"brep→point"</c> — pure BRep, no companion geometry or bbox; placement origin saved as POINT.</item>
  ///   <item><c>"bbox_fallback"</c> — no geometry stream; footprint derived from placement origin + bbox extents.</item>
  /// </list>
  /// </summary>
  public string GeometrySource { get; init; } = "";

  /// <summary>XY pairs in iModel spatial coordinates. Each element is <c>[x, y]</c>.</summary>
  public List<double[]> IModelCoordinates { get; init; } = new();

  /// <summary>
  /// Values of mapped properties extracted from the loadElementProps JSON response.
  /// Keys match <c>data_segment_properties.name</c> from Core DB (case-insensitive).
  /// </summary>
  public Dictionary<string, string?> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Geometry entry type keys found in the <c>geom[]</c> stream,
  /// e.g. <c>solidPrimitive</c>, <c>brep</c>, <c>lineString</c>.
  /// Empty when no <c>geom</c> property was present in the response.
  /// </summary>
  public List<string> GeometryTypes { get; init; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
// RpcQueryResult — queryRows response wrapper
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Parsed response from the RPC <c>queryRows</c> method.
/// Column names come from <c>meta[i].name</c>; rows are case-insensitive dictionaries.
/// </summary>
public sealed class RpcQueryResult
{
  /// <summary>RPC status code. 1 = Done/success; &gt;= 100 = error.</summary>
  public int Status { get; init; }

  public int RowCount { get; init; }

  /// <summary>Non-null when Status &gt;= 100.</summary>
  public string? ErrorMessage { get; init; }

  public List<Dictionary<string, object?>> Rows { get; init; } = new();

  /// <summary><c>true</c> when <see cref="Status"/> == 1 (Done).</summary>
  public bool IsSuccess => Status == 1;

  /// <summary>
  /// Parses a raw JSON response string from the <c>queryRows</c> RPC endpoint.
  /// Maps <c>data[row][col]</c> using <c>meta[col].name</c> as the key.
  /// </summary>
  public static RpcQueryResult Parse(string json)
  {
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var status = root.TryGetProperty("status", out var stEl) ? stEl.GetInt32() : -1;
    var rowCount = root.TryGetProperty("rowCount", out var rcEl) ? rcEl.GetInt32() : 0;

    string? errorMsg = null;
    if (root.TryGetProperty("error", out var errEl))
      errorMsg = errEl.ValueKind == JsonValueKind.String
          ? errEl.GetString()
          : errEl.GetRawText();

    // Build ordered column-name list from meta[].name
    var columns = new List<string>();
    if (root.TryGetProperty("meta", out var metaEl))
      foreach (var m in metaEl.EnumerateArray())
        columns.Add(m.TryGetProperty("name", out var n)
            ? n.GetString() ?? $"col{columns.Count}"
            : $"col{columns.Count}");

    var rows = new List<Dictionary<string, object?>>();
    if (root.TryGetProperty("data", out var dataEl))
    {
      foreach (var row in dataEl.EnumerateArray())
      {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (row.ValueKind == JsonValueKind.Array)
        {
          int i = 0;
          foreach (var val in row.EnumerateArray())
          {
            var key = i < columns.Count ? columns[i] : $"col{i}";
            dict[key] = ReadScalar(val);
            i++;
          }
        }
        else if (row.ValueKind == JsonValueKind.Object)
        {
          foreach (var prop in row.EnumerateObject())
            dict[prop.Name] = ReadScalar(prop.Value);
        }

        rows.Add(dict);
      }
    }

    return new RpcQueryResult
    {
      Status = status,
      RowCount = rowCount,
      ErrorMessage = errorMsg,
      Rows = rows
    };
  }

  private static object? ReadScalar(JsonElement el) => el.ValueKind switch
  {
    JsonValueKind.String => el.GetString(),
    JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.Null => null,
    _ => el.GetRawText()
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// ElementGeometryExtractor — parses geom[] JSON → XY point list
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Static helper that walks a <c>geom[]</c> <see cref="JsonElement"/> returned by
/// <c>loadElementProps</c> and collects 2D (XY only, Z ignored) coordinates.
/// </summary>
public static class ElementGeometryExtractor
{
  private const int ArcSampleCount = 24;

  /// <summary>
  /// Extracts XY points from a <c>geom[]</c> array.
  /// </summary>
  /// <returns>
  /// <c>(Points, UseFallback, HasBrep, TypesFound)</c>:
  /// <list type="bullet">
  ///   <item><c>Points</c>     — decoded XY pairs (empty when UseFallback is true).</item>
  ///   <item><c>UseFallback</c> — true when no decodable geometry was found.</item>
  ///   <item><c>HasBrep</c>   — true when at least one BRep entry was present in the stream.
  ///         When true AND <c>UseFallback</c> is false, companion geometry (lineString/loop/etc.)
  ///         was found alongside the BRep and should be used as the 2D footprint.
  ///         When true AND <c>UseFallback</c> is true, the element is BRep-only —
  ///         caller should fall back to placement origin as POINT.</item>
  ///   <item><c>TypesFound</c> — all geometry entry type keys seen (handled and unhandled).
  ///         Unknown types are prefixed with <c>?</c>. Use for diagnostics.</item>
  /// </list>
  /// </returns>
  public static (List<double[]> Points, bool UseFallback, bool HasBrep, List<string> TypesFound) Extract(JsonElement geomArray)
  {
    if (geomArray.ValueKind != JsonValueKind.Array)
      return (new(), true, false, new());

    var points = new List<double[]>();
    var typesFound = new List<string>();
    bool anyGeom = false;
    bool hasBrep = false;

    foreach (var entry in geomArray.EnumerateArray())
    {
      if (entry.ValueKind != JsonValueKind.Object) continue;

      if (entry.TryGetProperty("lineString", out var ls))
      {
        typesFound.Add("lineString"); anyGeom = true;
        ExtractLineString(ls, points);
      }
      else if (entry.TryGetProperty("loop", out var loop))
      {
        typesFound.Add("loop"); anyGeom = true;
        ExtractLoop(loop, points);
      }
      else if (entry.TryGetProperty("arc", out var arc))
      {
        typesFound.Add("arc"); anyGeom = true;
        ExtractArc(arc, points);
      }
      else if (entry.TryGetProperty("box", out var box))
      {
        typesFound.Add("box"); anyGeom = true;
        ExtractBox(box, points);
      }
      else if (entry.TryGetProperty("solidPrimitive", out var sp))
      {
        typesFound.Add("solidPrimitive"); anyGeom = true;
        ExtractSolidPrimitive(sp, points);
      }
      else if (entry.TryGetProperty("polyface", out var pf))
      {
        typesFound.Add("polyface"); anyGeom = true;
        ExtractPolyface(pf, points);
      }
      else if (entry.TryGetProperty("pointString", out var ps))
      {
        // Same wire format as lineString: array of [x,y,z] arrays
        typesFound.Add("pointString"); anyGeom = true;
        ExtractLineString(ps, points);
      }
      else if (entry.TryGetProperty("lineSegment", out var lseg))
      {
        // 2-point line segment: [[x1,y1,z1],[x2,y2,z2]] — common as a BRep centerline companion
        typesFound.Add("lineSegment"); anyGeom = true;
        ExtractLineString(lseg, points);
      }
      else if (entry.TryGetProperty("curveCollection", out var cc))
      {
        typesFound.Add("curveCollection"); anyGeom = true;
        ExtractLoop(cc, points);  // recurse into nested curve entries
      }
      else if (entry.TryGetProperty("path", out var path))
      {
        // Open curve path (sequence of connected curves) — same structure as loop/curveCollection
        typesFound.Add("path"); anyGeom = true;
        ExtractLoop(path, points);
      }
      else if (entry.TryGetProperty("brep", out _))
      {
        // BRep solid — requires a full geometry kernel to decode (not available in .NET).
        // When companion geometry exists alongside (lineString/lineSegment/loop/path/etc.),
        // those entries are already collected above and used as the 2D footprint (brep+geom).
        // When no companion is found, caller falls back to bbox → POLYGON, then origin → POINT.
        typesFound.Add("brep");
        hasBrep = true;
      }
      else if (entry.TryGetProperty("header", out _) ||
               entry.TryGetProperty("appearance", out _) ||
               entry.TryGetProperty("material", out _))
      {
        // geometry stream metadata — intentionally skipped
      }
      else
      {
        // Unknown / future geometry type — capture key names for diagnostics
        foreach (var p in entry.EnumerateObject())
          if (!typesFound.Contains($"?{p.Name}")) typesFound.Add($"?{p.Name}");
      }
    }

    // useFallback when no decodable geometry was found
    return (points, !anyGeom, hasBrep, typesFound);
  }

  // ── lineString: double[][] — each inner array is [x, y, z] ──────────────

  private static void ExtractLineString(JsonElement ls, List<double[]> pts)
  {
    if (ls.ValueKind != JsonValueKind.Array) return;
    foreach (var pt in ls.EnumerateArray())
    {
      if (pt.ValueKind != JsonValueKind.Array) continue;
      var arr = pt.EnumerateArray().ToArray();
      if (arr.Length >= 2)
        pts.Add(new[] { arr[0].GetDouble(), arr[1].GetDouble() });
    }
  }

  // ── loop: recurse into curve sub-entries ─────────────────────────────────
  // A loop may be encoded as an array of curve entries or as an object.

  private static void ExtractLoop(JsonElement loop, List<double[]> pts)
  {
    if (loop.ValueKind == JsonValueKind.Array)
    {
      foreach (var entry in loop.EnumerateArray())
      {
        if (entry.ValueKind != JsonValueKind.Object) continue;
        foreach (var prop in entry.EnumerateObject())
          DispatchByKey(prop.Name, prop.Value, pts);
      }
    }
    else if (loop.ValueKind == JsonValueKind.Object)
    {
      foreach (var prop in loop.EnumerateObject())
        DispatchByKey(prop.Name, prop.Value, pts);
    }
  }

  private static void DispatchByKey(string key, JsonElement value, List<double[]> pts)
  {
    switch (key)
    {
      case "lineString": ExtractLineString(value, pts); break;
      case "lineSegment": ExtractLineString(value, pts); break;  // same wire format
      case "arc": ExtractArc(value, pts); break;
      case "box": ExtractBox(value, pts); break;
      case "loop": ExtractLoop(value, pts); break;
      case "path": ExtractLoop(value, pts); break;  // open path, recurse same way
    }
  }

  // ── arc: { center:{x,y,z}, vectorX:{x,y,z}, vectorY:{x,y,z} } ──────────
  // Sample N=24 points: point = center + cos(angle)*vectorX + sin(angle)*vectorY

  private static void ExtractArc(JsonElement arc, List<double[]> pts)
  {
    if (arc.ValueKind != JsonValueKind.Object) return;
    if (!arc.TryGetProperty("center", out var cEl) ||
        !arc.TryGetProperty("vectorX", out var vxEl) ||
        !arc.TryGetProperty("vectorY", out var vyEl)) return;

    var (cx, cy) = ReadXY(cEl);
    var (vxx, vxy) = ReadXY(vxEl);
    var (vyx, vyy) = ReadXY(vyEl);

    for (int i = 0; i < ArcSampleCount; i++)
    {
      double angle = i * (2.0 * Math.PI / ArcSampleCount);
      pts.Add(new[]
      {
                cx + Math.Cos(angle) * vxx + Math.Sin(angle) * vyx,
                cy + Math.Cos(angle) * vxy + Math.Sin(angle) * vyy
            });
    }
  }

  // ── box: { baseOrigin, baseX, baseY, ... } → 4 base-plane corners ───────

  private static void ExtractBox(JsonElement box, List<double[]> pts)
  {
    if (box.ValueKind != JsonValueKind.Object) return;
    if (!box.TryGetProperty("baseOrigin", out var boEl)) return;

    var (ox, oy) = ReadXY(boEl);
    var (bxX, bxY) = box.TryGetProperty("baseX", out var bxEl) ? ReadXY(bxEl) : (1.0, 0.0);
    var (byX, byY) = box.TryGetProperty("baseY", out var byEl) ? ReadXY(byEl) : (0.0, 1.0);

    pts.Add(new[] { ox, oy });
    pts.Add(new[] { ox + bxX, oy + bxY });
    pts.Add(new[] { ox + bxX + byX, oy + bxY + byY });
    pts.Add(new[] { ox + byX, oy + byY });
  }

  // ── solidPrimitive: dispatch by sub-type ─────────────────────────────────
  // Common for structural elements: linearSweep (extrusion) is typical for Decks.

  private static void ExtractSolidPrimitive(JsonElement sp, List<double[]> pts)
  {
    if (sp.ValueKind != JsonValueKind.Object) return;
    foreach (var kindProp in sp.EnumerateObject())
    {
      var val = kindProp.Value;
      switch (kindProp.Name)
      {
        case "box":
          // Inline box inside solidPrimitive — same format as top-level box
          ExtractBox(val, pts);
          break;

        case "sphere":
          // { center:{x,y,z}, radius:n } — use centre as POINT
          if (val.TryGetProperty("center", out var sc))
          { var (x, y) = ReadXY(sc); pts.Add(new[] { x, y }); }
          break;

        case "cone":
          // { centerF:{x,y,z}, centerR:{x,y,z}, radiusF, radiusR }
          if (val.TryGetProperty("centerF", out var cf))
          { var (x, y) = ReadXY(cf); pts.Add(new[] { x, y }); }
          if (val.TryGetProperty("centerR", out var cr))
          { var (x, y) = ReadXY(cr); pts.Add(new[] { x, y }); }
          break;

        case "linearSweep":
          // Most common for structural Decks — extrusion of a 2D base curve.
          // Extract the base curve boundary as the 2D footprint.
          if (val.TryGetProperty("baseCurve", out var bc))
            ExtractLoop(bc, pts);
          break;

        case "rotationalSweep":
          if (val.TryGetProperty("baseCurve", out var rc))
            ExtractLoop(rc, pts);
          break;

        case "ruledSweep":
          // Array of contour curves — take the first contour as footprint
          if (val.TryGetProperty("contours", out var contours) &&
              contours.ValueKind == JsonValueKind.Array)
            foreach (var c in contours.EnumerateArray())
              ExtractLoop(c, pts);
          break;

        case "torusPipe":
          if (val.TryGetProperty("center", out var tc))
          { var (x, y) = ReadXY(tc); pts.Add(new[] { x, y }); }
          break;
      }
    }
  }

  // ── polyface: extract XY vertices from the mesh ───────────────────────────
  // The geometry stream wraps the mesh in { polyface: { indexedMesh: { point: [[x,y,z],...] } } }

  private static void ExtractPolyface(JsonElement pf, List<double[]> pts)
  {
    if (pf.ValueKind != JsonValueKind.Object) return;
    // Dig into indexedMesh if present, otherwise try pf directly
    var mesh = pf.TryGetProperty("indexedMesh", out var im) ? im : pf;
    if (!mesh.TryGetProperty("point", out var ptArr) || ptArr.ValueKind != JsonValueKind.Array)
      return;
    foreach (var pt in ptArr.EnumerateArray())
    {
      if (pt.ValueKind == JsonValueKind.Array)
      {
        var arr = pt.EnumerateArray().ToArray();
        if (arr.Length >= 2 && arr[0].ValueKind == JsonValueKind.Number)
          pts.Add(new[] { arr[0].GetDouble(), arr[1].GetDouble() });
      }
      else if (pt.ValueKind == JsonValueKind.Object)
      {
        var (x, y) = ReadXY(pt);
        pts.Add(new[] { x, y });
      }
    }
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static (double X, double Y) ReadXY(JsonElement el)
  {
    if (el.ValueKind != JsonValueKind.Object) return (0, 0);
    var x = el.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0.0;
    var y = el.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0.0;
    return (x, y);
  }

  /// <summary>
  /// Computes 4 world-space XY corners from placement origin + yaw + bbox extents.
  /// Used as the fallback when the geometry stream is absent or BRep-only.
  /// </summary>
  public static List<double[]> ComputeBBoxCorners(
      double originX, double originY, double yawDegrees,
      double bboxLowX, double bboxLowY, double bboxHighX, double bboxHighY)
  {
    double cos = Math.Cos(yawDegrees * Math.PI / 180.0);
    double sin = Math.Sin(yawDegrees * Math.PI / 180.0);

    (double x, double y)[] local =
    {
            (bboxLowX,  bboxLowY),
            (bboxHighX, bboxLowY),
            (bboxHighX, bboxHighY),
            (bboxLowX,  bboxHighY)
        };

    return local.Select(c => new[]
    {
            originX + c.x * cos - c.y * sin,
            originY + c.x * sin + c.y * cos
        }).ToList();
  }

  // Open-curve geometry type keys: represent linear/polyline features, not closed areas.
  private static readonly HashSet<string> _openTypes = new(StringComparer.OrdinalIgnoreCase)
        { "lineString", "lineSegment", "pointString", "arc", "path" };

  // Closed/area geometry type keys: represent 2D footprints or solids.
  private static readonly HashSet<string> _closedTypes = new(StringComparer.OrdinalIgnoreCase)
        { "loop", "box", "solidPrimitive", "polyface", "curveCollection" };

  /// <summary>
  /// Returns <c>true</c> when the coordinates represent an open linear feature
  /// (lineString / arc / path) rather than a closed area (loop / solidPrimitive / bbox).
  /// Use this to decide LINESTRING vs POLYGON WKT.
  /// </summary>
  /// <param name="types">The <see cref="RpcIModelElement.GeometryTypes"/> list.</param>
  /// <param name="geomSource">The <see cref="RpcIModelElement.GeometrySource"/> value.</param>
  public static bool IsLinear(IReadOnlyList<string> types, string geomSource)
  {
    // bbox and brep→bbox are always oriented bounding-box polygons
    if (geomSource is "bbox_fallback" or "brep\u2192bbox") return false;

    // If ANY closed type is present, the footprint is a polygon
    if (types.Any(t => _closedTypes.Contains(t))) return false;

    // If at least one open type is present (and no closed type), it's linear
    if (types.Any(t => _openTypes.Contains(t))) return true;

    // brep→point has exactly 1 point — ARS treats it as POINT, not LINE
    return false;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// RpcIModelService — iTwin RPC Interface over HTTP (no sidecar)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Calls the iTwin RPC Interface directly via HTTP — no Node.js sidecar involved.
/// Provides two operations:
/// <list type="bullet">
///   <item><c>queryRows</c>       — execute ECSQL and retrieve element properties.</item>
///   <item><c>loadElementProps</c> — retrieve element properties + geometry stream,
///         parse the stream to XY coords, fall back to placement bbox for BRep solids.</item>
/// </list>
/// <para>
/// The existing <see cref="IModelApiClient"/> workflow is completely unchanged.
/// </para>
/// <para>
/// <b>DI usage (optional):</b> register a named <see cref="HttpClient"/> called <c>"rpc"</c>
/// via <c>services.AddHttpClient("rpc")</c> and resolve it with
/// <c>IHttpClientFactory.CreateClient("rpc")</c>. For console use, pass <c>new HttpClient()</c>.
/// </para>
/// </summary>
public sealed class RpcIModelService : IDisposable
{
  private readonly HttpClient _http;
  private readonly string _baseUrl;
  private readonly string _iTwinId;
  private readonly string _iModelId;
  private readonly string _changesetId;
  private readonly int _changesetIndex;
  private readonly int _mode;
  private readonly ILogger<RpcIModelService> _log;

  private const string RpcInterface = "IModelReadRpcInterface-3.8.0";
  private const int MaxRetries = 3;     // up to 3 retries (4 total attempts)
  private const int RetryDelayMs = 500;

  private static readonly HttpStatusCode[] RetriableStatusCodes =
  {
        HttpStatusCode.TooManyRequests,   // 429
        HttpStatusCode.ServiceUnavailable // 503
    };

  /// <param name="httpClient">
  ///   Caller-owned <see cref="HttpClient"/>. When using DI supply the named client
  ///   <c>"rpc"</c>; in a plain console app use <c>new HttpClient()</c>.
  /// </param>
  /// <param name="logger">Optional; defaults to <see cref="NullLogger{T}"/> (no output).</param>
  public RpcIModelService(
      HttpClient httpClient,
      string baseUrl,
      string iTwinId,
      string iModelId,
      string changesetId,
      int mode = 1,
      int changesetIndex = 0,
      ILogger<RpcIModelService>? logger = null)
  {
    _http = httpClient;
    _baseUrl = baseUrl.TrimEnd('/');
    _iTwinId = iTwinId;
    _iModelId = iModelId;
    _changesetId = changesetId;
    _changesetIndex = changesetIndex;
    _mode = mode;
    _log = logger ?? NullLogger<RpcIModelService>.Instance;
  }

  // ── queryRows ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Executes an ECSQL statement via the RPC <c>queryRows</c> endpoint and returns
  /// the mapped rows as case-insensitive dictionaries.
  /// </summary>
  /// <param name="ecsql">ECSQL query string, e.g.
  ///   <c>SELECT e.ECInstanceId, e.UserLabel FROM BisCore.GeometricElement3d e LIMIT 50</c>
  /// </param>
  /// <param name="accessToken">Bearer token (no "Bearer " prefix).</param>
  public async Task<RpcQueryResult> QueryRowsAsync(
      string ecsql,
      string accessToken,
      CancellationToken ct = default,
      string? urlChangeset = null)
  {
    var url = BuildRpcUrl("queryRows", urlChangeset);

    // Browser-confirmed body format:
    //   Content-Type: text/plain
    //   Body: a JSON array of raw objects (NOT double-encoded strings)
    //   Header: x-protocol-version: 2  ← required for RPC gateway routing
    var iModelToken = new
    {
      key = $"{_iModelId}:{_changesetId}",
      iTwinId = _iTwinId,
      iModelId = _iModelId,
      changeset = new { id = _changesetId, index = _changesetIndex }
    };
    var queryParams = new
    {
      query = ecsql,
      args = new { },
      kind = 1,
      valueFormat = 0,
      rowFormat = 0,
      includeMetaData = true,
      limit = new { offset = 0, count = -1 }
    };
    var body = JsonSerializer.Serialize(new object[] { iModelToken, queryParams });

    var json = await PostRpcAsync(url, body, accessToken, ct);
    var result = RpcQueryResult.Parse(json);

    _log.LogInformation(
        "RPC queryRows | url={Url} status={Status} rowCount={RowCount}",
        url, result.Status, result.RowCount);

    if (!result.IsSuccess)
      _log.LogWarning(
          "RPC queryRows non-success | status={Status} error={Error}",
          result.Status, result.ErrorMessage);

    return result;
  }

  // ── loadElementProps ──────────────────────────────────────────────────────

  /// <summary>
  /// Retrieves element properties and geometry stream via <c>loadElementProps</c>
  /// with <c>wantGeometry:true</c>, parses the stream to XY coordinates and returns
  /// an <see cref="RpcIModelElement"/>.
  /// <para>
  /// Falls back to placement bbox corners when the geometry stream is absent,
  /// empty, or contains only BRep entries that cannot be decoded in .NET.
  /// </para>
  /// </summary>
  /// <param name="elementId">Hex element ID, e.g. <c>"0x1a3f"</c>.</param>
  /// <param name="accessToken">Bearer token (no "Bearer " prefix).</param>
  public async Task<RpcIModelElement?> LoadElementGeometryAsync(
      string elementId,
      string accessToken,
      CancellationToken ct = default,
      string? urlChangeset = null,
      IEnumerable<string>? extractProps = null)
  {
    var url = BuildRpcUrl("loadElementProps", urlChangeset);

    var lToken = new
    {
      key = $"{_iModelId}:{_changesetId}",
      iTwinId = _iTwinId,
      iModelId = _iModelId,
      changeset = new { id = _changesetId, index = _changesetIndex }
    };
    var lOpts = new { wantGeometry = true };
    var body = JsonSerializer.Serialize(new object[] { lToken, elementId, lOpts });

    var json = await PostRpcAsync(url, body, accessToken, ct);

    _log.LogInformation(
        "RPC loadElementProps | url={Url} elementId={ElementId}",
        url, elementId);

    using var doc = JsonDocument.Parse(json);
    var docRoot = doc.RootElement;
    // loadElementProps wraps the result in a 1-element array: [{...ElementProps...}]
    var root = docRoot.ValueKind == JsonValueKind.Array && docRoot.GetArrayLength() > 0
                        ? docRoot[0]
                        : docRoot;

    var userLabel = root.TryGetProperty("userLabel", out var ul) ? ul.GetString() : null;

    // ── Extract requested mapped properties from element JSON ───────────────
    // iTwin.js serialises custom properties in camelCase at the root level;
    // try camelCase first, then the original name from CoreDB.
    var props = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (extractProps != null && root.ValueKind == JsonValueKind.Object)
    {
      foreach (var propName in extractProps)
      {
        string? val = null;
        var cc = propName.Length > 0 ? char.ToLower(propName[0]) + propName[1..] : propName;
        if (root.TryGetProperty(cc, out var vcc) && vcc.ValueKind != JsonValueKind.Null)
          val = vcc.ValueKind == JsonValueKind.String ? vcc.GetString() : vcc.GetRawText();
        else if (!cc.Equals(propName, StringComparison.Ordinal) &&
                 root.TryGetProperty(propName, out var vp) && vp.ValueKind != JsonValueKind.Null)
          val = vp.ValueKind == JsonValueKind.String ? vp.GetString() : vp.GetRawText();
        props[propName] = val;
      }
    }

    // ── Geometry: try stream first, fall back to bbox ────────────────────
    // Priority chain:
    //   1. geom[] decoded (analytic geometry)              → geomSource = "geom"
    //   2. geom[] has brep + companion geometry alongside  → geomSource = "brep+geom"
    //   3. geom[] has brep only (no companion geometry)    → geomSource = "brep→point" (placement origin)
    //   4. geom[] unrecognised OR no geom[] + bbox present → geomSource = "bbox_fallback"
    //   5. no geom[] AND no bbox                          → coords empty → SKIPPED
    List<double[]> coords;
    string geomSource;
    List<string> geomTypes;

    if (root.TryGetProperty("geom", out var geomEl)
        && geomEl.ValueKind == JsonValueKind.Array
        && geomEl.GetArrayLength() > 0)
    {
      var (points, useFallback, hasBrep, typesFound) = ElementGeometryExtractor.Extract(geomEl);
      geomTypes = typesFound;

      if (!useFallback && points.Count > 0)
      {
        coords = points;
        // When BRep is present alongside decodable geometry, those companion entries
        // (lineString / loop / solidPrimitive / etc.) are the simplified 2D layout
        // of the solid — use them as-is; distinguish source label for visibility.
        geomSource = hasBrep ? "brep+geom" : "geom";
      }
      else if (hasBrep)
      {
        // Pure BRep — no companion geometry found.
        // Priority:
        //   1. placement.origin + bbox → oriented POLYGON (best available footprint)
        //   2. placement.origin only   → POINT (last resort)
        //   3. neither                  → empty (SKIPPED)
        var bboxCoords = ReadBBoxFallback(root);
        if (bboxCoords.Count > 0)
        {
          coords = bboxCoords;
          geomSource = "brep→bbox";
        }
        else
        {
          var origin = ReadOriginPoint(root);
          coords = origin != null ? new List<double[]> { origin } : new List<double[]>();
          geomSource = "brep→point";
        }
      }
      else
      {
        // geom[] present but all entries unrecognised — try bbox corners
        coords = ReadBBoxFallback(root);
        geomSource = "bbox_fallback";
      }
    }
    else
    {
      // No geom[] in response — try bbox corners
      geomTypes = new List<string>();
      coords = ReadBBoxFallback(root);
      geomSource = "bbox_fallback";
    }

    return new RpcIModelElement
    {
      ElementId = elementId,
      UserLabel = userLabel,
      GeometrySource = geomSource,
      IModelCoordinates = coords,
      GeometryTypes = geomTypes,
      Properties = props
    };
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private string BuildRpcUrl(string methodName, string? urlChangeset = null)
  {
    // When urlChangeset is null, use the configured changeset.
    // When explicitly set to "" (empty), the /changeset/ segment is omitted so
    // the backend opens the latest available checkpoint instead of a specific one.
    var cs = urlChangeset ?? _changesetId;
    var csSegment = string.IsNullOrEmpty(cs) ? "" : $"/changeset/{cs}";
    return $"{_baseUrl}/imodel/rpc/v5/mode/{_mode}/context/{_iTwinId}" +
           $"/imodel/{_iModelId}{csSegment}/{RpcInterface}-{methodName}";
  }

  /// <summary>
  /// Reads a single component from a Vec3 that may be serialised as either
  /// an object <c>{x,y,z}</c> or a positional array <c>[x, y, z]</c>.
  /// </summary>
  private static double ReadVec3(JsonElement el, string propName, int arrayIndex)
  {
    if (el.ValueKind == JsonValueKind.Array)
      return el.GetArrayLength() > arrayIndex && el[arrayIndex].ValueKind == JsonValueKind.Number
             ? el[arrayIndex].GetDouble() : 0;
    if (el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(propName, out var v) &&
        v.ValueKind == JsonValueKind.Number)
      return v.GetDouble();
    return 0;
  }

  /// <summary>
  /// Extracts placement origin as a single <c>[x, y]</c> point.
  /// Used as the POINT fallback for BRep solids that cannot be decoded in .NET.
  /// ARS only supports POINT, LINE, and POLYGON — BRep maps to POINT.
  /// </summary>
  private static double[]? ReadOriginPoint(JsonElement root)
  {
    if (root.ValueKind != JsonValueKind.Object) return null;
    if (!root.TryGetProperty("placement", out var pl) || pl.ValueKind != JsonValueKind.Object) return null;
    if (!pl.TryGetProperty("origin", out var orig) || orig.ValueKind == JsonValueKind.Null) return null;
    return new[] { ReadVec3(orig, "x", 0), ReadVec3(orig, "y", 1) };
  }

  /// <summary>
  /// Reads placement origin + yaw + bbox from the loadElementProps response root
  /// and delegates to <see cref="ElementGeometryExtractor.ComputeBBoxCorners"/>.
  /// Handles both object <c>{x,y,z}</c> and positional-array <c>[x,y,z]</c>
  /// formats for origin / bbox corners (varies by element type).
  /// </summary>
  private static List<double[]> ReadBBoxFallback(JsonElement root)
  {
    // Guard: some responses are not an object at all
    if (root.ValueKind != JsonValueKind.Object)
      return [];

    double originX = 0, originY = 0, yaw = 0;
    double lowX = 0, lowY = 0;
    double highX = 0, highY = 0;

    // Track whether data was actually present in the JSON.
    // All fields default to 0, so without these flags we cannot distinguish
    // "element at origin with zero bbox" from "element with no placement at all"
    // — the latter must NOT produce geometry (bug: was returning (0,0)x4).
    bool hasOrigin = false;
    bool hasBBox = false;

    if (root.TryGetProperty("placement", out var pl) && pl.ValueKind == JsonValueKind.Object)
    {
      // origin may be {x,y,z} or [x,y,z]
      if (pl.TryGetProperty("origin", out var orig) && orig.ValueKind != JsonValueKind.Null)
      {
        hasOrigin = true;
        originX = ReadVec3(orig, "x", 0);
        originY = ReadVec3(orig, "y", 1);
      }

      // angles may be {yaw,pitch,roll} or [yaw,pitch,roll]
      if (pl.TryGetProperty("angles", out var ang) && ang.ValueKind != JsonValueKind.Null)
      {
        if (ang.ValueKind == JsonValueKind.Object &&
            ang.TryGetProperty("yaw", out var yawEl) &&
            yawEl.ValueKind == JsonValueKind.Number)
          yaw = yawEl.GetDouble();
        else if (ang.ValueKind == JsonValueKind.Array && ang.GetArrayLength() > 0)
          yaw = ang[0].ValueKind == JsonValueKind.Number ? ang[0].GetDouble() : 0;
      }

      // bbox.low / bbox.high — each may be {x,y,z} or [x,y,z]
      if (pl.TryGetProperty("bbox", out var bbox) && bbox.ValueKind == JsonValueKind.Object)
      {
        if (bbox.TryGetProperty("low", out var bLow) && bLow.ValueKind != JsonValueKind.Null &&
            bbox.TryGetProperty("high", out var bHigh) && bHigh.ValueKind != JsonValueKind.Null)
        {
          hasBBox = true;
          lowX = ReadVec3(bLow, "x", 0);
          lowY = ReadVec3(bLow, "y", 1);
          highX = ReadVec3(bHigh, "x", 0);
          highY = ReadVec3(bHigh, "y", 1);
        }
      }
    }

    // Root-level bBoxLow / bBoxHigh (some element types expose these directly)
    if (root.TryGetProperty("bBoxLow", out var bl) && bl.ValueKind != JsonValueKind.Null &&
        root.TryGetProperty("bBoxHigh", out var bh) && bh.ValueKind != JsonValueKind.Null)
    {
      hasBBox = true;
      lowX = ReadVec3(bl, "x", 0);
      lowY = ReadVec3(bl, "y", 1);
      highX = ReadVec3(bh, "x", 0);
      highY = ReadVec3(bh, "y", 1);
    }

    // ── Decide what to return based on what was actually present ──────────
    // Require BOTH origin AND bbox to produce geometry from the fallback path.
    // • No origin + no bbox   → element has no spatial data at all → skip
    // • Origin only, no bbox  → element has no physical extent (graphic/annotation) → skip
    // • Both present          → compute 4 oriented world-space corners → POLYGON
    //
    // Note: BRep elements never reach here — they are handled before ReadBBoxFallback
    // is called and use ReadOriginPoint() which returns a POINT from the origin alone.
    if (!hasOrigin || !hasBBox)
      return [];

    // Both origin and bbox present — compute 4 oriented corners (→ POLYGON).
    return ElementGeometryExtractor.ComputeBBoxCorners(
        originX, originY, yaw, lowX, lowY, highX, highY);
  }

  // ── HTTP POST with retry (429 / 503) ──────────────────────────────────────

  private async Task<string> PostRpcAsync(
      string url,
      string body,
      string accessToken,
      CancellationToken ct)
  {
    for (int attempt = 0; attempt <= MaxRetries; attempt++)
    {
      using var req = new HttpRequestMessage(HttpMethod.Post, url);
      // Content-Type must be text/plain (confirmed from browser DevTools)
      // Body is a raw JSON object array — NOT double-encoded strings
      req.Content = new StringContent(body, Encoding.UTF8, "text/plain");
      req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
      req.Headers.Accept.ParseAdd("application/json");
      // x-protocol-version: 2 is REQUIRED — the RPC gateway uses it for routing
      req.Headers.Add("x-protocol-version", "2");

      HttpResponseMessage res;
      try
      {
        res = await _http.SendAsync(req, ct);
      }
      catch (Exception ex) when (attempt < MaxRetries)
      {
        _log.LogWarning(
            "RPC attempt {Attempt}/{Max} network error — retrying in {Delay}ms: {Message}",
            attempt + 1, MaxRetries, RetryDelayMs, ex.Message);
        await Task.Delay(RetryDelayMs, ct);
        continue;
      }

      if (RetriableStatusCodes.Contains(res.StatusCode) && attempt < MaxRetries)
      {
        _log.LogWarning(
            "RPC {StatusCode} on attempt {Attempt}/{Max} — retrying in {Delay}ms",
            (int)res.StatusCode, attempt + 1, MaxRetries, RetryDelayMs);
        res.Dispose();
        await Task.Delay(RetryDelayMs, ct);
        continue;
      }

      if (!res.IsSuccessStatusCode)
      {
        var err = await res.Content.ReadAsStringAsync(ct);
        Console.WriteLine($"[RPC ERROR] {(int)res.StatusCode} {res.ReasonPhrase} from {url}");
        Console.WriteLine($"[RPC ERROR] Response body: {err}");
        _log.LogError("RPC {StatusCode} from {Url}: {Body}", (int)res.StatusCode, url, err);
        throw new HttpRequestException(
            $"RPC {(int)res.StatusCode} {res.ReasonPhrase} — {err}",
            null,
            res.StatusCode);
      }

      return await res.Content.ReadAsStringAsync(ct);
    }

    throw new InvalidOperationException(
        $"RPC call to {url} failed after {MaxRetries} retries.");
  }

  public void Dispose() => _http.Dispose();
}
