using System.Globalization;
using System.Text;

namespace NSearcher.Benchmarks;

internal static class CliToolComparisonChartWriter
{
    public static void WriteSvg(string outputPath, IReadOnlyList<CliComparisonResult> comparisons)
    {
        if (comparisons.Count == 0)
        {
            return;
        }

        const int width = 1320;
        const int rowHeight = 136;
        const int topPadding = 124;
        const int bottomPadding = 64;
        const int leftLabelWidth = 310;
        const int chartWidth = 900;
        const int barHeight = 18;
        const int firstBarOffset = 32;
        const int barStep = 30;

        var height = topPadding + (comparisons.Count * rowHeight) + bottomPadding;
        var maxMedian = comparisons
            .SelectMany(GetMedianValues)
            .DefaultIfEmpty(1)
            .Max();

        var builder = new StringBuilder();
        builder.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">""");
        builder.AppendLine("""  <title id="title">NSearcher vs ripgrep vs grep vs ugrep benchmark comparison</title>""");
        builder.AppendLine("""  <desc id="desc">Grouped horizontal bar chart comparing NSearcher, ripgrep, grep and ugrep median execution times by scenario.</desc>""");
        builder.AppendLine($"""  <rect width="{width}" height="{height}" fill="#f7f4ee" />""");
        builder.AppendLine("""  <text x="40" y="52" font-family="Segoe UI, Arial, sans-serif" font-size="30" font-weight="700" fill="#1d3124">NSearcher vs ripgrep vs grep vs ugrep</text>""");
        builder.AppendLine("""  <text x="40" y="82" font-family="Segoe UI, Arial, sans-serif" font-size="16" fill="#496154">Median wall-clock time by scenario. Lower is better.</text>""");

        for (var tick = 0; tick <= 4; tick++)
        {
            var x = leftLabelWidth + (chartWidth * tick / 4.0);
            builder.AppendLine($"""  <line x1="{Format(x)}" y1="{topPadding - 18}" x2="{Format(x)}" y2="{height - bottomPadding + 10}" stroke="#d4cec3" stroke-width="1" />""");
            var tickValue = maxMedian * tick / 4.0;
            builder.AppendLine($"""  <text x="{Format(x)}" y="{topPadding - 26}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="13" fill="#6d766f">{Format(tickValue)} ms</text>""");
        }

        for (var index = 0; index < comparisons.Count; index++)
        {
            var comparison = comparisons[index];
            var rowTop = topPadding + (index * rowHeight);
            var labelY = rowTop + 18;
            var verdictY = rowTop + 122;

            builder.AppendLine($"""  <text x="40" y="{labelY}" font-family="Segoe UI, Arial, sans-serif" font-size="18" font-weight="600" fill="#223229">{Escape(comparison.ScenarioName)}</text>""");

            var bars = BuildBars(comparison, maxMedian, leftLabelWidth, chartWidth);
            for (var barIndex = 0; barIndex < bars.Count; barIndex++)
            {
                var bar = bars[barIndex];
                var y = rowTop + firstBarOffset + (barIndex * barStep);
                builder.AppendLine($"""  <text x="40" y="{y + 13}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="{bar.LabelColor}">{Escape(bar.Label)}</text>""");
                builder.AppendLine($"""  <rect x="{leftLabelWidth}" y="{y}" width="{Format(bar.Width)}" height="{barHeight}" rx="6" fill="{bar.BarColor}" />""");
                builder.AppendLine($"""  <text x="{Format(leftLabelWidth + bar.Width + 12)}" y="{y + 13}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="{bar.ValueColor}">{Format(bar.MedianMs)} ms</text>""");
            }

            builder.AppendLine($"""  <text x="40" y="{verdictY}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="#4b5c52">{Escape(BuildVerdict(comparison))}</text>""");
        }

        builder.AppendLine("</svg>");
        File.WriteAllText(outputPath, builder.ToString(), Encoding.UTF8);
    }

    private static List<ChartBar> BuildBars(
        CliComparisonResult comparison,
        double maxMedian,
        int leftLabelWidth,
        int chartWidth)
    {
        var bars = new List<ChartBar>(capacity: 4)
        {
            new(
                "NSearcher",
                comparison.NSearcher.MedianMs,
                chartWidth * (comparison.NSearcher.MedianMs / maxMedian),
                "#1f8f63",
                "#0b5d3a",
                "#20543b")
        };

        if (comparison.Ripgrep is not null)
        {
            bars.Add(new(
                "ripgrep",
                comparison.Ripgrep.MedianMs,
                chartWidth * (comparison.Ripgrep.MedianMs / maxMedian),
                "#9aa3b2",
                "#6b7280",
                "#384152"));
        }

        if (comparison.Grep is not null)
        {
            bars.Add(new(
                "grep",
                comparison.Grep.MedianMs,
                chartWidth * (comparison.Grep.MedianMs / maxMedian),
                "#c8892c",
                "#8a5e16",
                "#7a4f0a"));
        }

        if (comparison.Ugrep is not null)
        {
            bars.Add(new(
                "ugrep",
                comparison.Ugrep.MedianMs,
                chartWidth * (comparison.Ugrep.MedianMs / maxMedian),
                "#7c63c9",
                "#4e3aa0",
                "#43308a"));
        }

        return bars;
    }

    private static IEnumerable<double> GetMedianValues(CliComparisonResult comparison)
    {
        yield return comparison.NSearcher.MedianMs;

        if (comparison.Ripgrep is not null)
        {
            yield return comparison.Ripgrep.MedianMs;
        }

        if (comparison.Grep is not null)
        {
            yield return comparison.Grep.MedianMs;
        }

        if (comparison.Ugrep is not null)
        {
            yield return comparison.Ugrep.MedianMs;
        }
    }

    private static string BuildVerdict(CliComparisonResult comparison)
    {
        var contenders = new List<(string Name, double MedianMs)>
        {
            ("NSearcher", comparison.NSearcher.MedianMs)
        };

        if (comparison.Ripgrep is not null)
        {
            contenders.Add(("ripgrep", comparison.Ripgrep.MedianMs));
        }

        if (comparison.Grep is not null)
        {
            contenders.Add(("grep", comparison.Grep.MedianMs));
        }

        if (comparison.Ugrep is not null)
        {
            contenders.Add(("ugrep", comparison.Ugrep.MedianMs));
        }

        var fastest = contenders.MinBy(static contender => contender.MedianMs);
        var second = contenders
            .Where(contender => !string.Equals(contender.Name, fastest.Name, StringComparison.Ordinal))
            .OrderBy(static contender => contender.MedianMs)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(second.Name))
        {
            return $"Fastest: {fastest.Name}";
        }

        var advantage = ((second.MedianMs - fastest.MedianMs) / second.MedianMs) * 100.0;
        return $"Fastest: {fastest.Name} by {Format(advantage)}% vs {second.Name}";
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private readonly record struct ChartBar(
        string Label,
        double MedianMs,
        double Width,
        string BarColor,
        string LabelColor,
        string ValueColor);
}
