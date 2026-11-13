using System.Globalization;
using NetTopologySuite.Geometries;

namespace Dendro.Core.Sampling;

public enum GrtsSampleSizeMode { Intensity, FixedPerStratum, PerStratum, Precision }
public enum GrtsSelectionType { Equal, Proportional, Categories }

public sealed record GrtsStratumDesign(string Stratum, int BaseSize, int ReplacementSize, int FrameSize,
    double EligibleAreaHa, double RequestedMinimumDistance, double? AchievedMinimumDistance,
    int Attempts, bool DistanceSatisfied, bool ProbabilitiesAreExact);

public static partial class ForestSampler
{
    private sealed record GrtsPoint(Stand Stand, Point Point, double AreaUnitHa, double Auxiliary, string Category);

    public static int PlanGrtsSampleSize(double confidencePercent, double relativeErrorPercent, double cvPercent)
    {
        if (!double.IsFinite(confidencePercent) || confidencePercent <= 0 || confidencePercent >= 100 ||
            !double.IsFinite(relativeErrorPercent) || relativeErrorPercent <= 0 || !double.IsFinite(cvPercent) || cvPercent <= 0)
            throw new ArgumentException("Informe confiança entre 0 e 100%, erro relativo e CV positivos.");
        double z = MathNet.Numerics.Distributions.Normal.InvCDF(0, 1, 0.5 + confidencePercent / 200);
        double size = Math.Ceiling(Math.Pow(z * cvPercent / relativeErrorPercent, 2));
        if (size > 100_000) throw new ArgumentException("O dimensionamento excede 100 mil parcelas por estrato. Revise erro e CV.");
        return Math.Max(2, (int)size);
    }

    public static string GrtsStratumKey(IReadOnlyDictionary<string, object?> attributes, string standId, string field) => field switch
    {
        "" => standId,
        "*" => "Todos",
        _ => RequiredAttribute(attributes, field)
    };

    private static string RequiredAttribute(IReadOnlyDictionary<string, object?> attributes, string field)
    {
        if (!attributes.TryGetValue(field, out var value) || value is null || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
            throw new ArgumentException($"O campo '{field}' está ausente ou vazio em uma feição.");
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    private static ForestSamplingResult RunGrts(List<Stand> stands, string sourcePath, ForestSamplingOptions o, CancellationToken token)
    {
        if (o.GrtsPointDensity is < 1 or > 1000 || o.GrtsMaxAttempts is < 1 or > 1000 || o.GrtsReplacementCount < 0 ||
            o.GrtsMinimumDistance < 0 || !double.IsFinite(o.GrtsMinimumDistance))
            throw new ArgumentException("GRTS: densidade e tentativas devem estar entre 1 e 1000; distância e substitutas não podem ser negativas.");
        var groups = stands.GroupBy(stand => GrtsStratumKey(stand.Attributes, stand.Id, o.GrtsStratumField)).ToArray();
        var allocated = stands.ToDictionary(stand => stand.Id, _ => new List<Candidate>(), StringComparer.Ordinal);
        var designs = new List<GrtsStratumDesign>();
        var warnings = new List<string>();
        var random = new Random(o.Seed);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            int n = o.GrtsSampleSizeMode switch
            {
                GrtsSampleSizeMode.FixedPerStratum => o.GrtsFixedSampleSize,
                GrtsSampleSizeMode.PerStratum => o.GrtsStratumSampleSizes.TryGetValue(group.Key, out int count) ? count
                    : throw new ArgumentException($"Informe a quantidade de parcelas para o estrato '{group.Key}'."),
                GrtsSampleSizeMode.Precision => PlanGrtsSampleSize(o.GrtsConfidencePercent, o.GrtsRelativeErrorPercent, o.GrtsCvPercent),
                _ => group.Sum(stand => stand.Required)
            };
            if (n is < 1 or > 100_000) throw new ArgumentException($"Estrato '{group.Key}': informe entre 1 e 100 mil parcelas de base.");
            int total = checked(n + o.GrtsReplacementCount);
            int target = checked(total * o.GrtsPointDensity);
            if (target > 1_000_000) throw new ArgumentException($"Estrato '{group.Key}': reduza a densidade ou a quantidade; o limite é um milhão de candidatos.");
            var shapes = group.Select(stand => (Stand: stand, Shape: SafeBuffer(stand.Shape, o.EdgeBufferMeters))).ToArray();
            double sumArea = shapes.Sum(item => item.Shape.Area);
            Geometry union = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(shapes.Select(item => item.Shape).ToArray());
            if (sumArea - union.Area > Math.Max(0.01, sumArea * 1e-8))
                throw new ArgumentException($"Estrato '{group.Key}': os polígonos elegíveis estão sobrepostos. Corrija as sobreposições antes do GRTS.");
            var frame = new List<GrtsPoint>();
            foreach (var (stand, shape) in shapes)
            {
                double auxiliary = 1;
                string category = "";
                if (o.GrtsSelectionType == GrtsSelectionType.Proportional &&
                    (!double.TryParse(RequiredAttribute(stand.Attributes, o.GrtsAuxiliaryField), NumberStyles.Float, CultureInfo.InvariantCulture, out auxiliary) ||
                     !double.IsFinite(auxiliary) || auxiliary <= 0))
                    throw new ArgumentException($"Talhão '{stand.Id}': o campo auxiliar precisa ser numérico e positivo.");
                if (o.GrtsSelectionType == GrtsSelectionType.Categories) category = RequiredAttribute(stand.Attributes, o.GrtsCategoryField);
                int standTarget = Math.Max(1, (int)Math.Ceiling(target * shape.Area / sumArea));
                var candidates = CreateHexFrame(shape, standTarget, random, token, 1);
                if (candidates.Count == 0) throw new InvalidOperationException($"Não foi possível criar candidatos dentro do talhão '{stand.Id}'.");
                double unitHa = shape.Area / 10_000d / candidates.Count;
                frame.AddRange(candidates.Select(point => new GrtsPoint(stand, point, unitHa, auxiliary, category)));
            }
            if (frame.Count > 1_000_000 || total > frame.Count)
                throw new ArgumentException($"Estrato '{group.Key}': quadro de candidatos insuficiente ou acima do limite. Revise a densidade.");
            double[] basePi = GrtsProbabilities(frame, n, n, group.Key, o);
            double[] drawPi = GrtsProbabilities(frame, total, n, group.Key, o);
            int[]? best = null;
            double bestMinimum = -1;
            int tries = 0;
            int maxTries = o.GrtsMinimumDistance > 0 ? o.GrtsMaxAttempts : 1;
            for (; tries < maxTries; tries++)
            {
                token.ThrowIfCancellationRequested();
                int addressSeed = random.Next();
                var ordered = Enumerable.Range(0, frame.Count)
                    .Select(index => (Index: index, Address: HierarchicalAddress(frame[index].Point, union.EnvelopeInternal, addressSeed)))
                    .OrderBy(item => item.Address).ThenBy(item => item.Index).ToArray();
                int[] draw = SelectOnProbabilityLine(ordered.Select(item => item.Index).ToArray(), drawPi, total, random);
                if (o.GrtsReplacementCount > 0)
                {
                    var addresses = ordered.ToDictionary(item => item.Index, item => item.Address);
                    draw = draw.OrderBy(index => ReverseAddress(addresses[index])).ToArray();
                }
                double achieved = MinimumPairDistance(draw, frame, token);
                if (achieved > bestMinimum) { best = draw; bestMinimum = achieved; }
                if (achieved + 1e-8 >= o.GrtsMinimumDistance) { tries++; break; }
            }
            bool met = bestMinimum + 1e-8 >= o.GrtsMinimumDistance;
            // Conditioning on distance and splitting an oversample changes the
            // realized design. Retain nominal probabilities but mark them as such.
            bool exact = o.GrtsMinimumDistance == 0 && o.GrtsReplacementCount == 0;
            for (int index = 0; index < best!.Length; index++)
            {
                var point = frame[best[index]];
                double pi = basePi[best[index]];
                bool reserve = index >= n;
                allocated[point.Stand.Id].Add(new Candidate(point.Point, reserve ? 2 : 1, pi, 1 / pi, frame.Count,
                    group.Key, reserve ? "Reserva" : "Base", reserve ? index - n + 1 : 0, exact, point.AreaUnitHa / pi));
            }
            designs.Add(new GrtsStratumDesign(group.Key, n, o.GrtsReplacementCount, frame.Count, sumArea / 10_000,
                o.GrtsMinimumDistance, double.IsPositiveInfinity(bestMinimum) ? null : bestMinimum, tries, met, exact));
            if (!met) warnings.Add($"Estrato '{group.Key}': distância de {o.GrtsMinimumDistance:G} m não atingida em {tries} tentativas; obtido {bestMinimum:G5} m. O tamanho da amostra foi mantido.");
        }
        if (o.GrtsMinimumDistance > 0 || o.GrtsReplacementCount > 0)
            warnings.Add("PI_APROX e PESO são valores nominais do desenho de base; o ajuste de distância ou a seleção de reservas altera o desenho. PI_EXATA=false identifica essa condição.");
        if (o.GrtsSampleSizeMode == GrtsSampleSizeMode.Precision)
            warnings.Add("Quantidade planejada por aproximação normal usando o CV informado; a precisão alcançada depende das medições de campo.");
        var samples = Number(stands, allocated, o);
        return new ForestSamplingResult(samples, stands.Count, 0, stands.Sum(stand => stand.AreaHa), designs.Sum(design => design.BaseSize),
            sourcePath, o.Method, warnings.Count == 0 ? null : string.Join("\n", warnings), o, designs);
    }

    private static double[] GrtsProbabilities(List<GrtsPoint> frame, int size, int baseSize, string stratum, ForestSamplingOptions o)
    {
        if (o.GrtsSelectionType != GrtsSelectionType.Categories)
            return NormalizeProbabilities(frame.Select(point => point.AreaUnitHa * point.Auxiliary).ToArray(), size);
        if (!o.GrtsCategorySampleSizes.TryGetValue(stratum, out var quotas))
            throw new ArgumentException($"Informe as quantidades esperadas por categoria do estrato '{stratum}'.");
        var groups = frame.Select((point, index) => (point.Category, Index: index)).GroupBy(item => item.Category).ToArray();
        if (groups.Any(group => !quotas.TryGetValue(group.Key, out int value) || value <= 0) ||
            quotas.Keys.Except(groups.Select(group => group.Key)).Any() || quotas.Values.Sum() != baseSize)
            throw new ArgumentException($"Estrato '{stratum}': todas as categorias precisam de quantidade esperada positiva; a soma deve ser {baseSize}.");
        var probabilities = new double[frame.Count];
        foreach (var group in groups)
        {
            int[] indices = group.Select(item => item.Index).ToArray();
            double expected = quotas[group.Key] * (size / (double)baseSize);
            if (expected > indices.Length) throw new ArgumentException($"Categoria '{group.Key}': aumente a densidade de candidatos.");
            var pi = NormalizeProbabilities(indices.Select(index => frame[index].AreaUnitHa).ToArray(), expected);
            for (int i = 0; i < indices.Length; i++) probabilities[indices[i]] = pi[i];
        }
        return probabilities;
    }

    private static double[] NormalizeProbabilities(double[] mass, double size)
    {
        if (size <= 0 || size > mass.Length || mass.Any(value => !double.IsFinite(value) || value <= 0))
            throw new ArgumentException("Probabilidades inválidas: use massas positivas e quantidade compatível com o quadro.");
        double max = mass.Max();
        var scaled = mass.Select(value => value / max).ToArray();
        var pi = new double[mass.Length];
        var active = Enumerable.Range(0, mass.Length).ToList();
        double remaining = size;
        while (active.Count > 0)
        {
            double sum = active.Sum(index => scaled[index]);
            var certainty = active.Where(index => scaled[index] * remaining / sum >= 1).ToArray();
            if (certainty.Length == 0)
            {
                foreach (int index in active) pi[index] = scaled[index] * remaining / sum;
                break;
            }
            foreach (int index in certainty) { pi[index] = 1; remaining--; active.Remove(index); }
            if (remaining <= 1e-12) break;
        }
        if (pi.Any(value => value <= 0)) throw new ArgumentException("A variável auxiliar é desproporcional: algumas probabilidades ficaram numericamente nulas.");
        return pi;
    }

    private static int[] SelectOnProbabilityLine(int[] ordered, double[] pi, int size, Random random)
    {
        double start = random.NextDouble(), sum = 0;
        int next = 0;
        var result = new List<int>(size);
        foreach (int index in ordered)
        {
            sum += pi[index];
            if (next < size && start + next < sum)
            {
                result.Add(index); next++;
            }
        }
        if (result.Count != size || result.Distinct().Count() != size)
            throw new InvalidOperationException("A seleção GRTS não preservou o tamanho da amostra. Revise os parâmetros numéricos.");
        return result.ToArray();
    }

    private static ulong ReverseAddress(ulong address)
    {
        ulong reverse = 0;
        for (int i = 0; i < 32; i++) { reverse = (reverse << 2) | (address & 3); address >>= 2; }
        return reverse;
    }

    private static double MinimumPairDistance(int[] selected, List<GrtsPoint> frame, CancellationToken token)
    {
        double minimum = double.PositiveInfinity;
        var points = selected.Select(index => frame[index].Point).OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        var active = new SortedSet<(double Y, int Index)>();
        int left = 0;
        for (int i = 0; i < points.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            while (left < i && points[i].X - points[left].X > minimum)
            {
                active.Remove((points[left].Y, left)); left++;
            }
            foreach (var neighbor in active.GetViewBetween((points[i].Y - minimum, int.MinValue), (points[i].Y + minimum, int.MaxValue)))
                minimum = Math.Min(minimum, points[i].Distance(points[neighbor.Index]));
            if (minimum == 0) return 0;
            active.Add((points[i].Y, i));
        }
        return minimum;
    }
}
