using System.Globalization;
using System.Text;

namespace NSearcher.Benchmarks;

internal static class RipgrepComparisonChartWriter
{
    public static void WriteSvg(string outputPath, IReadOnlyList<CliComparisonResult> comparisons)
    {
        if (comparisons.Count == 0)
        {
            return;
        }

        const int width = 1200;
        const int rowHeight = 112;
        const int topPadding = 120;
        const int bottomPadding = 60;
        const int leftLabelWidth = 290;
        const int chartWidth = 820;
        const int barHeight = 22;

        var height = topPadding + (comparisons.Count * rowHeight) + bottomPadding;
        var maxMedian = comparisons
            .SelectMany(result => new[] { result.NSearcher.MedianMs, result.Ripgrep!.MedianMs })
            .DefaultIfEmpty(1)
            .Max();

        var builder = new StringBuilder();
        builder.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">""");
        builder.AppendLine("""  <title id="title">NSearcher vs ripgrep benchmark comparison</title>""");
        builder.AppendLine("""  <desc id="desc">Grouped horizontal bar chart comparing NSearcher and ripgrep median execution times by scenario.</desc>""");
        builder.AppendLine($"""  <rect width="{width}" height="{height}" fill="#f7f4ee" />""");
        builder.AppendLine("""  <text x="40" y="52" font-family="Segoe UI, Arial, sans-serif" font-size="30" font-weight="700" fill="#1d3124">NSearcher vs ripgrep</text>""");
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
            var rgY = rowTop + 34;
            var nsY = rowTop + 66;
            var winnerY = rowTop + 100;
            var rgWidth = chartWidth * (comparison.Ripgrep!.MedianMs / maxMedian);
            var nsWidth = chartWidth * (comparison.NSearcher.MedianMs / maxMedian);
            var advantage = comparison.NSearcherAdvantagePercent!.Value;
            var verdict = advantage >= 0
                ? $"NSearcher faster by {Format(advantage)}%"
                : $"ripgrep faster by {Format(Math.Abs(advantage))}%";

            builder.AppendLine($"""  <text x="40" y="{labelY}" font-family="Segoe UI, Arial, sans-serif" font-size="18" font-weight="600" fill="#223229">{Escape(comparison.ScenarioName)}</text>""");

            builder.AppendLine($"""  <text x="40" y="{rgY + 15}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="#6b7280">ripgrep</text>""");
            builder.AppendLine($"""  <rect x="{leftLabelWidth}" y="{rgY}" width="{Format(rgWidth)}" height="{barHeight}" rx="7" fill="#9aa3b2" />""");
            builder.AppendLine($"""  <text x="{Format(leftLabelWidth + rgWidth + 12)}" y="{rgY + 16}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="#384152">{Format(comparison.Ripgrep.MedianMs)} ms</text>""");

            builder.AppendLine($"""  <text x="40" y="{nsY + 15}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="#0b5d3a">NSearcher</text>""");
            builder.AppendLine($"""  <rect x="{leftLabelWidth}" y="{nsY}" width="{Format(nsWidth)}" height="{barHeight}" rx="7" fill="#1f8f63" />""");
            builder.AppendLine($"""  <text x="{Format(leftLabelWidth + nsWidth + 12)}" y="{nsY + 16}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="#20543b">{Format(comparison.NSearcher.MedianMs)} ms</text>""");

            builder.AppendLine($"""  <text x="40" y="{winnerY}" font-family="Segoe UI, Arial, sans-serif" font-size="14" fill="#4b5c52">{Escape(verdict)}</text>""");
        }

        builder.AppendLine("</svg>");
        File.WriteAllText(outputPath, builder.ToString(), Encoding.UTF8);
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
