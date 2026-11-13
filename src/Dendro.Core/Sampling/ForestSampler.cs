using System.Globalization;
using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;

namespace Dendro.Core.Sampling;

public enum ForestSamplingMethod
{
    ExistingSystematicRounds,
    RandomStartSystematic,
    SpatiallyBalanced,
    Grts,
}

public sealed record ForestSamplingOptions
{
    public string IdField { get; init; } = "REF_ID";
    public double HectaresPerPlot { get; init; } = 3;
    public double EdgeBufferMeters { get; init; } = -20;
    public double MinimumDistanceMeters { get; init; } = 56.42;
    public double MinimumAreaHa { get; init; } = 3;
    public double IntermediateAreaHa { get; init; } = 6;
    public int PlotsBelowMinimum { get; init; } = 1;
    public int PlotsBelowIntermediate { get; init; } = 2;
    public ForestSamplingMethod Method { get; init; } = ForestSamplingMethod.ExistingSystematicRounds;
    public bool FirstRoundUsesVertices { get; init; }
    public bool SequentialNumbering { get; init; } = true;
    public bool SequenceByProject { get; init; } = true;
    public string ProjectField { get; init; } = "PROJETO";
    public bool SequenceWithinStandByCoordinates { get; init; } = true;
    public int Seed { get; init; } = 42;
    public string GrtsStratumField { get; init; } = "";
    public GrtsSampleSizeMode GrtsSampleSizeMode { get; init; }
    public int GrtsFixedSampleSize { get; init; } = 10;
    public Dictionary<string, int> GrtsStratumSampleSizes { get; init; } = new(StringComparer.Ordinal);
    public GrtsSelectionType GrtsSelectionType { get; init; }
    public string GrtsAuxiliaryField { get; init; } = "";
    public string GrtsCategoryField { get; init; } = "";
    public Dictionary<string, Dictionary<string, int>> GrtsCategorySampleSizes { get; init; } = new(StringComparer.Ordinal);
    public int GrtsPointDensity { get; init; } = 10;
    public double GrtsMinimumDistance { get; init; }
    public int GrtsMaxAttempts { get; init; } = 10;
    public int GrtsReplacementCount { get; init; }
    public double GrtsConfidencePercent { get; init; } = 95;
    public double GrtsRelativeErrorPercent { get; init; } = 10;
    public double GrtsCvPercent { get; init; } = 30;
}

public sealed record ForestSample(
    string StandId, int Sequence, int Round, double X, double Y,
    IReadOnlyDictionary<string, object?> Attributes,
    double InclusionProbability = 1, double DesignWeight = 1, int CandidateFrameSize = 1,
    string Stratum = "", string SiteUse = "Base", int ReplacementOrder = 0,
    bool ProbabilityIsExact = false, double AreaWeightHa = 0);

public sealed record ForestSamplingResult(
    IReadOnlyList<ForestSample> Samples, int StandCount, int IncompleteStandCount,
    double TotalAreaHa, int RequiredPlots, string SourcePath, ForestSamplingMethod Method, string? Warning,
    ForestSamplingOptions? Options = null, IReadOnlyList<GrtsStratumDesign>? GrtsDesign = null);

/// <summary>Point allocation for forest stands stored in a projected, metre-based polygon shapefile.</summary>
public static partial class ForestSampler
{
    public static string[] ReadAttributeFields(string shapefilePath)
    {
        string dbfPath = Path.ChangeExtension(shapefilePath, ".dbf");
        if (!File.Exists(dbfPath)) throw new FileNotFoundException("Tabela de atributos (.dbf) não encontrada.", dbfPath);
        using var reader = new NetTopologySuite.IO.Esri.Dbf.DbfReader(dbfPath);
        return reader.Fields.Select(field => field.Name).ToArray();
    }

    public static Dictionary<string, object?>[] ReadAttributeRecords(string shapefilePath)
    {
        using var reader = new NetTopologySuite.IO.Esri.Dbf.DbfReader(Path.ChangeExtension(shapefilePath, ".dbf"));
        return reader.Select(record => record.GetNames().ToDictionary(name => name, name => (object?)record[name], StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private sealed record Stand(string Id, Geometry Shape, Dictionary<string, object?> Attributes, double AreaHa, int Required);
    private sealed record Candidate(Point Point, int Round, double InclusionProbability = 1, double DesignWeight = 1, int FrameSize = 1,
        string Stratum = "", string SiteUse = "Base", int ReplacementOrder = 0, bool ProbabilityIsExact = false, double AreaWeightHa = 0);

    public static ForestSamplingResult Run(string shapefilePath, ForestSamplingOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(shapefilePath)) throw new FileNotFoundException("Shapefile não encontrado.", shapefilePath);
        if (!File.Exists(Path.ChangeExtension(shapefilePath, ".prj")))
            throw new InvalidDataException("O shapefile precisa ter .prj para confirmar que as coordenadas estão em metros.");
        string wkt = File.ReadAllText(Path.ChangeExtension(shapefilePath, ".prj"));
        if (!wkt.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) && !wkt.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O shapefile precisa usar um sistema projetado em metros. Reprojete para UTM antes da amostragem.");
        if (wkt.Contains("UNIT[\"Foot", StringComparison.OrdinalIgnoreCase) || wkt.Contains("UNIT[\"US survey foot", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O sistema do shapefile está em pés. Reprojete para um sistema métrico antes da amostragem.");
        Validate(options);

        var stands = new List<Stand>();
        foreach (var feature in Shapefile.ReadAllFeatures(shapefilePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Geometry? shape = feature.Geometry;
            if (shape is not (Polygon or MultiPolygon)) continue;
            if (!shape.IsValid) shape = shape.Buffer(0);
            if (shape.IsEmpty || shape.Area <= 0) continue;
            var attrs = feature.Attributes.GetNames().ToDictionary(name => name, name => (object?)feature.Attributes[name], StringComparer.OrdinalIgnoreCase);
            string id = attrs.TryGetValue(options.IdField, out var rawId) && rawId != null
                ? Convert.ToString(rawId, CultureInfo.InvariantCulture) ?? ""
                : $"T{stands.Count + 1:0000}";
            if (string.IsNullOrWhiteSpace(id)) id = $"T{stands.Count + 1:0000}";
            double areaHa = shape.Area / 10_000d;
            if (stands.Any(s => string.Equals(s.Id, id, StringComparison.Ordinal)))
            {
                if (options.Method == ForestSamplingMethod.Grts) throw new ArgumentException($"O identificador '{id}' está duplicado. Escolha um campo que identifique cada talhão para o GRTS.");
                id = $"{id}_{stands.Count + 1:0000}";
            }
            stands.Add(new Stand(id, shape, attrs, areaHa, RequiredPlots(areaHa, options)));
        }
        if (stands.Count == 0) throw new InvalidDataException("Nenhum polígono válido foi encontrado no shapefile.");
        if (options.Method == ForestSamplingMethod.Grts) return RunGrts(stands, shapefilePath, options, cancellationToken);

        var allocated = stands.ToDictionary(s => s.Id, _ => new List<Candidate>(), StringComparer.Ordinal);
        var random = new Random(options.Seed);
        if (options.Method == ForestSamplingMethod.ExistingSystematicRounds)
            AllocateExisting(stands, allocated, options, cancellationToken);
        else if (options.Method == ForestSamplingMethod.RandomStartSystematic)
            AllocateRandomGrid(stands, allocated, options, random, cancellationToken);
        else
            AllocateBalanced(stands, allocated, options, random, cancellationToken);

        var samples = Number(stands, allocated, options);
        int incomplete = stands.Count(s => allocated[s.Id].Count < s.Required);
        string? warning = incomplete > 0 ? $"{incomplete} talhão(ões) ficaram abaixo da quantidade solicitada por causa da área útil ou da distância mínima." : null;
        return new ForestSamplingResult(samples, stands.Count, incomplete, stands.Sum(s => s.AreaHa), stands.Sum(s => s.Required), shapefilePath, options.Method, warning, options);
    }

    public static int RequiredPlots(double areaHa, ForestSamplingOptions options)
    {
        if (areaHa < options.MinimumAreaHa) return options.PlotsBelowMinimum;
        if (areaHa < options.IntermediateAreaHa) return options.PlotsBelowIntermediate;
        return Math.Max(options.PlotsBelowIntermediate, (int)Math.Ceiling(areaHa / options.HectaresPerPlot));
    }

    private static void AllocateExisting(List<Stand> stands, Dictionary<string, List<Candidate>> result, ForestSamplingOptions o, CancellationToken token)
    {
        double cell = Math.Sqrt(o.HectaresPerPlot * 10_000);
        var extent = stands.Select(s => s.Shape.EnvelopeInternal).Aggregate((a, b) => { a.ExpandToInclude(b); return a; });
        var centers = Grid(extent, cell, true, 0, 0);
        var vertices = Grid(extent, cell, false, 0, 0);
        var first = o.FirstRoundUsesVertices ? vertices : centers;
        var second = o.FirstRoundUsesVertices ? centers : vertices;
        var buffered = stands.Select(s => (Stand: s, Shape: SafeBuffer(s.Shape, o.EdgeBufferMeters))).ToArray();
        foreach (var (stand, shape) in buffered)
        {
            token.ThrowIfCancellationRequested();
            foreach (var point in first.Where(shape.Contains))
            {
                if (!Unique(point, AllPoints(result))) continue;
                result[stand.Id].Add(new Candidate(point, 1));
            }
        }
        AllocateFallback(stands, buffered, result, second, 2, o.MinimumDistanceMeters, token);
        AllocateFallback(stands, buffered, result, Array.Empty<Point>(), 3, o.MinimumDistanceMeters, token);
        foreach (var stand in stands)
        {
            if (result[stand.Id].Count != 0) continue;
            Point point = stand.Shape.Centroid;
            if (!stand.Shape.Contains(point)) point = stand.Shape.InteriorPoint;
            result[stand.Id].Add(new Candidate(point, 4));
        }
    }

    private static void AllocateFallback(List<Stand> stands, (Stand Stand, Geometry Shape)[] buffered,
        Dictionary<string, List<Candidate>> result, IEnumerable<Point> grid, int round, double minDistance, CancellationToken token)
    {
        foreach (var (stand, shape) in buffered)
        {
            token.ThrowIfCancellationRequested();
            int missing = stand.Required - result[stand.Id].Count;
            if (missing <= 0) continue;
            var candidates = round == 3
                ? Fragments(shape, AllPoints(result), minDistance).Select(g => g.InteriorPoint)
                : grid.Where(shape.Contains);
            foreach (var point in candidates)
            {
                if (missing <= 0) break;
                if (!Unique(point, AllPoints(result)) || !FarEnough(point, AllPoints(result), minDistance)) continue;
                result[stand.Id].Add(new Candidate(point, round)); missing--;
            }
        }
    }

    private static IEnumerable<Geometry> Fragments(Geometry shape, IEnumerable<Point> existing, double radius)
    {
        Geometry available = shape;
        foreach (var point in existing.Where(p => p.Distance(shape) <= radius)) available = available.Difference(point.Buffer(radius));
        var parts = available is GeometryCollection collection ? collection.Geometries : [available];
        return parts.Where(g => g is Polygon or MultiPolygon).SelectMany(g => g is MultiPolygon multi ? multi.Geometries : [g])
            .OrderByDescending(g => g.Area);
    }

    private static void AllocateRandomGrid(List<Stand> stands, Dictionary<string, List<Candidate>> result, ForestSamplingOptions o, Random random, CancellationToken token)
    {
        double cell = Math.Sqrt(o.HectaresPerPlot * 10_000);
        var extent = stands.Select(s => s.Shape.EnvelopeInternal).Aggregate((a, b) => { a.ExpandToInclude(b); return a; });
        var grid = Grid(extent, cell, true, random.NextDouble() * cell, random.NextDouble() * cell);
        foreach (var stand in stands)
        {
            token.ThrowIfCancellationRequested();
            var shape = SafeBuffer(stand.Shape, o.EdgeBufferMeters);
            var selected = grid.Where(shape.Contains).OrderBy(p => p.Distance(stand.Shape.Centroid));
            foreach (var point in selected)
            {
                if (result[stand.Id].Count >= stand.Required) break;
                if (!Unique(point, AllPoints(result)) || !FarEnough(point, AllPoints(result), o.MinimumDistanceMeters)) continue;
                result[stand.Id].Add(new Candidate(point, 1));
            }
            if (result[stand.Id].Count < stand.Required)
                AddBalancedWithin(stand, shape, result, o.MinimumDistanceMeters, random, token, 2);
        }
    }

    private static void AllocateBalanced(List<Stand> stands, Dictionary<string, List<Candidate>> result, ForestSamplingOptions o, Random random, CancellationToken token)
    {
        foreach (var stand in stands)
        {
            token.ThrowIfCancellationRequested();
            AddBalancedWithin(stand, SafeBuffer(stand.Shape, o.EdgeBufferMeters), result, o.MinimumDistanceMeters, random, token, 1);
        }
    }

    private static List<Point> CreateHexFrame(Geometry polygon, int sampleSize, Random random, CancellationToken token, int density = 10)
    {
        int target = checked(Math.Max(sampleSize * density, sampleSize));
        if (target > 1_000_000) throw new ArgumentException("O quadro excede um milhão de candidatos por estrato. Reduza a densidade ou a quantidade de parcelas.");
        var envelope = polygon.EnvelopeInternal;
        double area = Math.Max(polygon.Area, 1);
        double spacing = Math.Sqrt((2 * area) / (Math.Sqrt(3) * target));
        spacing = Math.Max(spacing, 0.05);
        List<Point> points = [];

        // Retry with progressively denser hexagonal grids to handle narrow or
        // highly irregular polygons where a random grid origin misses cells.
        for (int attempt = 0; attempt < 9; attempt++)
        {
            token.ThrowIfCancellationRequested();
            points = HexGrid(polygon, spacing, random.NextDouble() * spacing,
                random.NextDouble() * spacing * Math.Sqrt(3) / 2, token);
            if (points.Count >= target) break;
            spacing *= 0.7;
        }

        // A rejection-sampling supplement keeps small/concave frames from
        // returning fewer candidates than the requested base sample.
        int attempts = 0, maxAttempts = Math.Max(20_000, target * 2_000);
        while (points.Count < target && points.Count < 1_000_000 && attempts++ < maxAttempts)
        {
            if ((attempts & 1023) == 0) token.ThrowIfCancellationRequested();
            double x = envelope.MinX + random.NextDouble() * envelope.Width;
            double y = envelope.MinY + random.NextDouble() * envelope.Height;
            var point = new Point(x, y);
            if (polygon.Contains(point)) points.Add(point);
        }
        return points;
    }

    private static List<Point> HexGrid(Geometry polygon, double spacing, double offsetX, double offsetY, CancellationToken token)
    {
        var e = polygon.EnvelopeInternal;
        double rowSpacing = spacing * Math.Sqrt(3) / 2;
        double xStart = e.MinX - spacing + offsetX;
        double yStart = e.MinY - rowSpacing + offsetY;
        var points = new List<Point>();
        int row = 0;
        for (double y = yStart; y <= e.MaxY + rowSpacing; y += rowSpacing, row++)
        {
            if ((row & 255) == 0) token.ThrowIfCancellationRequested();
            double shift = (row & 1) == 0 ? 0 : spacing / 2;
            for (double x = xStart + shift; x <= e.MaxX + spacing; x += spacing)
            {
                var point = new Point(x, y);
                if (polygon.Contains(point)) points.Add(point);
            }
        }
        return points;
    }

    private static ulong HierarchicalAddress(Point point, Envelope envelope, int seed)
    {
        double side = Math.Max(envelope.Width, envelope.Height);
        if (side <= 0) return 0;
        double minX = envelope.Centre.X - side / 2, minY = envelope.Centre.Y - side / 2;
        uint x = ToGridCoordinate((point.X - minX) / side);
        uint y = ToGridCoordinate((point.Y - minY) / side);
        ulong address = 0, physicalPrefix = 0;
        for (int level = 0; level < 32; level++)
        {
            int bit = 31 - level;
            int quadrant = (int)(((x >> bit) & 1) | (((y >> bit) & 1) << 1));
            int label = RandomizedQuadrantLabel(quadrant, unchecked((ulong)(uint)seed), physicalPrefix, level);
            address = (address << 2) | (uint)label;
            physicalPrefix = (physicalPrefix << 2) | (uint)quadrant;
        }
        return address;
    }

    private static uint ToGridCoordinate(double value)
    {
        double scaled = Math.Clamp(value, 0, Math.BitDecrement(1d)) * 4_294_967_296d;
        return (uint)scaled;
    }

    private static int RandomizedQuadrantLabel(int quadrant, ulong seed, ulong prefix, int level)
    {
        Span<int> labels = stackalloc int[] { 0, 1, 2, 3 };
        ulong state = Mix64(seed ^ Mix64(prefix + 0x9E3779B97F4A7C15UL) ^ ((ulong)level * 0xD1B54A32D192ED03UL));
        for (int i = labels.Length - 1; i > 0; i--)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong value = Mix64(state);
            int j = (int)(value % (uint)(i + 1));
            (labels[i], labels[j]) = (labels[j], labels[i]);
        }
        for (int label = 0; label < labels.Length; label++) if (labels[label] == quadrant) return label;
        return quadrant;
    }

    private static ulong Mix64(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static void AddBalancedWithin(Stand stand, Geometry shape, Dictionary<string, List<Candidate>> result,
        double minDistance, Random random, CancellationToken token, int round)
    {
        int missing = stand.Required - result[stand.Id].Count;
        if (missing <= 0 || shape.IsEmpty) return;
        double spacing = Math.Sqrt(Math.Max(shape.Area, 1) / Math.Max(stand.Required * 16d, 1));
        spacing = Math.Max(10, Math.Min(spacing, 100));
        var candidates = Grid(shape.EnvelopeInternal, spacing, true, spacing / 2, spacing / 2).Where(shape.Contains).ToList();
        if (candidates.Count == 0) candidates.Add(shape.InteriorPoint);
        var chosen = result[stand.Id].Select(c => c.Point).ToList();
        var external = AllPoints(result).Where(point => !chosen.Contains(point)).ToList();
        while (missing > 0 && candidates.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            Point next;
            if (chosen.Count == 0 && external.Count == 0) next = candidates[random.Next(candidates.Count)];
            else next = candidates.MaxBy(p => chosen.Concat(external).Min(p.Distance))!;
            if (Unique(next, chosen.Concat(external)) && (chosen.Count == 0 && external.Count == 0 || chosen.Concat(external).Min(next.Distance) >= minDistance))
            {
                result[stand.Id].Add(new Candidate(next, round)); chosen.Add(next); missing--;
            }
            candidates.Remove(next);
        }
    }

    private static List<ForestSample> Number(List<Stand> stands, Dictionary<string, List<Candidate>> allocated, ForestSamplingOptions o)
    {
        var rows = new List<(Stand Stand, Candidate Candidate)>();
        foreach (var stand in stands)
        foreach (var point in allocated[stand.Id]) rows.Add((stand, point));
        if (o.SequentialNumbering)
        {
            rows = rows.OrderBy(r => o.SequenceByProject && o.ProjectField.Length > 0 && r.Stand.Attributes.TryGetValue(o.ProjectField, out var p) ? Convert.ToString(p, CultureInfo.InvariantCulture) : "")
                .ThenBy(r => r.Stand.Id[..Math.Min(14, r.Stand.Id.Length)], StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Stand.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => o.SequenceWithinStandByCoordinates ? r.Candidate.Point.X : r.Candidate.Round)
                .ThenBy(r => o.SequenceWithinStandByCoordinates ? r.Candidate.Point.Y : 0).ToList();
        }
        else rows = rows.OrderBy(r => r.Candidate.Round).ThenBy(r => r.Stand.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int globalSequence = 0;
        var output = new List<ForestSample>();
        foreach (var row in rows)
        {
            string lot = row.Stand.Id[..Math.Min(14, row.Stand.Id.Length)];
            string project = o.SequenceByProject && o.ProjectField.Length > 0 && row.Stand.Attributes.TryGetValue(o.ProjectField, out var projectValue)
                ? Convert.ToString(projectValue, CultureInfo.InvariantCulture) ?? "" : "";
            string sequenceKey = o.SequentialNumbering ? (o.SequenceByProject ? project + "\u001f" + lot : lot) : "__ALL__";
            counts.TryGetValue(sequenceKey, out int seq); counts[sequenceKey] = ++seq;
            globalSequence++;
            var attrs = new Dictionary<string, object?>(row.Stand.Attributes, StringComparer.OrdinalIgnoreCase);
            output.Add(new ForestSample(row.Stand.Id, o.SequentialNumbering ? seq : globalSequence, row.Candidate.Round,
                row.Candidate.Point.X, row.Candidate.Point.Y, attrs, row.Candidate.InclusionProbability,
                row.Candidate.DesignWeight, row.Candidate.FrameSize, row.Candidate.Stratum, row.Candidate.SiteUse,
                row.Candidate.ReplacementOrder, row.Candidate.ProbabilityIsExact, row.Candidate.AreaWeightHa));
        }
        return output;
    }

    public static void WriteShapefile(ForestSamplingResult result, string path)
    {
        var features = new List<IFeature>();
        var factory = new GeometryFactory();
        foreach (var sample in result.Samples)
        {
            var attributes = new AttributesTable
            {
                { "STAND_ID", sample.StandId }, { "SEQ", sample.Sequence }, { "ORDEM", sample.Round },
                { "X", sample.X }, { "Y", sample.Y }
            };
            if (result.Method == ForestSamplingMethod.Grts)
            {
                attributes.Add("PI_APROX", sample.InclusionProbability); attributes.Add("PESO", sample.DesignWeight);
                attributes.Add("N_FRAME", sample.CandidateFrameSize); attributes.Add("ESTRATO", sample.Stratum);
                attributes.Add("TIPO", sample.SiteUse); attributes.Add("RES_ORDEM", sample.ReplacementOrder);
                attributes.Add("PI_EXATA", sample.ProbabilityIsExact); attributes.Add("PESO_HA", sample.AreaWeightHa);
            }
            var usedNames = new HashSet<string>(attributes.GetNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var (name, raw) in sample.Attributes)
            {
                string field = SafeFieldName(name, usedNames);
                object value = raw switch
                {
                    null => "",
                    string or int or short or byte or long or double or float or decimal or bool or DateTime => raw,
                    _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""
                };
                attributes.Add(field, value);
            }
            features.Add(new Feature(factory.CreatePoint(new Coordinate(sample.X, sample.Y)), attributes));
        }
        Shapefile.WriteAllFeatures(features, path);
        string sourcePrj = Path.ChangeExtension(result.SourcePath, ".prj");
        if (File.Exists(sourcePrj)) File.Copy(sourcePrj, Path.ChangeExtension(path, ".prj"), true);
    }

    public static void WriteCsv(ForestSamplingResult result, string path)
    {
        var fields = result.Samples.SelectMany(s => s.Attributes.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        string[] designFields = result.Method == ForestSamplingMethod.Grts ? ["PI_APROX", "PESO", "N_FRAME", "ESTRATO", "TIPO", "RES_ORDEM", "PI_EXATA", "PESO_HA"] : [];
        writer.WriteLine(string.Join(';', new[] { "STAND_ID", "SEQ", "ORDEM", "X", "Y" }.Concat(designFields).Concat(fields).Select(EscapeCsv)));
        foreach (var sample in result.Samples)
        {
            string[] designValues = result.Method == ForestSamplingMethod.Grts ? [
                sample.InclusionProbability.ToString("R", CultureInfo.InvariantCulture), sample.DesignWeight.ToString("R", CultureInfo.InvariantCulture),
                sample.CandidateFrameSize.ToString(CultureInfo.InvariantCulture), sample.Stratum, sample.SiteUse,
                sample.ReplacementOrder.ToString(CultureInfo.InvariantCulture), sample.ProbabilityIsExact.ToString(),
                sample.AreaWeightHa.ToString("R", CultureInfo.InvariantCulture)] : [];
            writer.WriteLine(string.Join(';', new[] { sample.StandId, sample.Sequence.ToString(CultureInfo.InvariantCulture), sample.Round.ToString(CultureInfo.InvariantCulture),
                sample.X.ToString("R", CultureInfo.InvariantCulture), sample.Y.ToString("R", CultureInfo.InvariantCulture) }.Concat(designValues)
                .Concat(fields.Select(field => Convert.ToString(sample.Attributes.GetValueOrDefault(field), CultureInfo.InvariantCulture) ?? "")).Select(EscapeCsv)));
        }
    }

    public static void WriteMapSvg(ForestSamplingResult result, string path)
    {
        var polygons = Shapefile.ReadAllFeatures(result.SourcePath).Select(f => f.Geometry).Where(g => g is Polygon or MultiPolygon).ToArray();
        if (polygons.Length == 0) throw new InvalidDataException("Não há polígonos para desenhar o mapa.");
        var bounds = polygons.Select(g => g.EnvelopeInternal).Aggregate((a, b) => { a.ExpandToInclude(b); return a; });
        foreach (var sample in result.Samples) bounds.ExpandToInclude(sample.X, sample.Y);
        double width = 1200, height = 800, pad = 38;
        double scale = Math.Min((width - 2 * pad) / Math.Max(bounds.Width, 1), (height - 2 * pad) / Math.Max(bounds.Height, 1));
        double DrawX(double x) => pad + (x - bounds.MinX) * scale;
        double DrawY(double y) => height - pad - (y - bounds.MinY) * scale;
        var svg = new StringBuilder();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Mapa das parcelas amostrais\">");
        svg.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"#f5f8fa\"/><g fill=\"#dcebd7\" stroke=\"#577b52\" stroke-width=\"1.5\" fill-rule=\"evenodd\">");
        foreach (var geometry in polygons)
        foreach (var polygon in geometry is MultiPolygon multi ? multi.Geometries.Cast<Polygon>() : [((Polygon)geometry)])
        {
            var rings = new[] { polygon.ExteriorRing }.Concat(Enumerable.Range(0, polygon.NumInteriorRings).Select(polygon.GetInteriorRingN));
            foreach (var ring in rings)
            {
                var points = ring.Coordinates;
                svg.Append("<path d=\"");
                for (int i = 0; i < points.Length; i++) svg.Append(i == 0 ? "M" : "L").Append(DrawX(points[i].X).ToString("0.##", CultureInfo.InvariantCulture)).Append(',').Append(DrawY(points[i].Y).ToString("0.##", CultureInfo.InvariantCulture));
                svg.AppendLine("Z\"/>");
            }
        }
        svg.AppendLine("</g>");
        var colors = new[] { "#1565c0", "#f57c00", "#7b1fa2", "#c62828" };
        foreach (var sample in result.Samples)
        {
            string color = colors[Math.Clamp(sample.Round - 1, 0, colors.Length - 1)];
            svg.AppendLine($"<circle cx=\"{DrawX(sample.X).ToString("0.##", CultureInfo.InvariantCulture)}\" cy=\"{DrawY(sample.Y).ToString("0.##", CultureInfo.InvariantCulture)}\" r=\"5\" fill=\"{color}\" stroke=\"white\" stroke-width=\"1.5\"><title>{System.Security.SecurityElement.Escape(sample.StandId)} · SEQ {sample.Sequence} · D{sample.Round}</title></circle>");
        }
        svg.AppendLine("</svg>");
        File.WriteAllText(path, svg.ToString(), new UTF8Encoding(false));
    }

    public static void WriteDesign(ForestSamplingResult result, string path) => File.WriteAllText(path,
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1, Source = result.SourcePath, Method = result.Method.ToString(), Options = result.Options,
            Strata = result.GrtsDesign, RequiredBasePlots = result.RequiredPlots,
            BasePlots = result.Samples.Count(sample => sample.SiteUse == "Base"),
            ReplacementPlots = result.Samples.Count(sample => sample.SiteUse == "Reserva"), result.Warning,
            Note = "PI_EXATA refere-se ao quadro finito de candidatos; PESO_HA representa expansão em hectares da área elegível. Valores de reservas são nominais do desenho de base."
        }, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        }), new UTF8Encoding(false));

    private static string EscapeCsv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string SafeFieldName(string name, HashSet<string> used)
    {
        string stem = new string(name.Where(char.IsLetterOrDigit).Take(10).ToArray());
        if (string.IsNullOrEmpty(stem)) stem = "FIELD";
        string candidate = stem;
        int suffix = 1;
        while (!used.Add(candidate))
        {
            string tail = suffix++.ToString(CultureInfo.InvariantCulture);
            candidate = stem[..Math.Min(stem.Length, 10 - tail.Length)] + tail;
        }
        return candidate;
    }
    private static Geometry SafeBuffer(Geometry shape, double distance) { var buffered = shape.Buffer(distance); return buffered.IsEmpty ? shape : buffered; }
    private static bool FarEnough(Point point, IEnumerable<Point> others, double distance) => distance <= 0 || others.All(other => point.Distance(other) >= distance);
    private static bool Unique(Point point, IEnumerable<Point> others) => others.All(other => point.Distance(other) > 0.001);
    private static IEnumerable<Point> AllPoints(Dictionary<string, List<Candidate>> allocated) => allocated.Values.SelectMany(list => list).Select(c => c.Point);

    private static IEnumerable<Point> Grid(Envelope e, double step, bool centers, double offsetX, double offsetY)
    {
        double startX = e.MinX + (centers ? step / 2 : 0) + offsetX;
        double startY = e.MinY + (centers ? step / 2 : 0) + offsetY;
        for (double x = startX; x <= e.MaxX; x += step)
        for (double y = startY; y <= e.MaxY; y += step) yield return new Point(x, y);
    }

    private static void Validate(ForestSamplingOptions o)
    {
        if (!double.IsFinite(o.EdgeBufferMeters)) throw new ArgumentException("Informe um recuo de borda finito.");
        if (!Enum.IsDefined(o.Method) || !Enum.IsDefined(o.GrtsSampleSizeMode) || !Enum.IsDefined(o.GrtsSelectionType))
            throw new ArgumentException("Método ou configuração de seleção inválida.");
        if (o.HectaresPerPlot <= 0 || !double.IsFinite(o.HectaresPerPlot)) throw new ArgumentOutOfRangeException(nameof(o.HectaresPerPlot));
        if (o.MinimumDistanceMeters < 0 || !double.IsFinite(o.MinimumDistanceMeters)) throw new ArgumentOutOfRangeException(nameof(o.MinimumDistanceMeters));
        if (o.MinimumAreaHa < 0 || o.IntermediateAreaHa < o.MinimumAreaHa || o.PlotsBelowMinimum < 1 || o.PlotsBelowIntermediate < 1)
            throw new ArgumentException("Revise as faixas de área e as quantidades mínimas por talhão.");
    }
}
