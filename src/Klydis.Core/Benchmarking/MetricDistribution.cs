using System;
using System.Collections.Generic;
using System.Linq;

namespace Klydis.Core.Benchmarking;

/// <summary>
/// Statistical distribution summary of benchmark metric samples across iterations.
/// </summary>
public record MetricDistribution(
    double Mean,
    double Median,
    double Min,
    double Max,
    double P95,
    double StdDev
)
{
    /// <summary>
    /// Computes statistical distribution metrics from a sequence of numerical samples.
    /// </summary>
    public static MetricDistribution FromValues(IEnumerable<double> values)
    {
        var list = values?.OrderBy(v => v).ToList() ?? new List<double>();
        if (list.Count == 0)
        {
            return new MetricDistribution(0, 0, 0, 0, 0, 0);
        }

        double mean = Math.Round(list.Average(), 2);
        double median = Math.Round(list.Count % 2 == 1 
            ? list[list.Count / 2] 
            : (list[(list.Count / 2) - 1] + list[list.Count / 2]) / 2.0, 2);
        double min = Math.Round(list.First(), 2);
        double max = Math.Round(list.Last(), 2);
        
        int p95Index = (int)Math.Ceiling(0.95 * list.Count) - 1;
        double p95 = Math.Round(list[Math.Clamp(p95Index, 0, list.Count - 1)], 2);

        double variance = list.Sum(v => Math.Pow(v - mean, 2)) / list.Count;
        double stdDev = Math.Round(Math.Sqrt(variance), 2);

        return new MetricDistribution(mean, median, min, max, p95, stdDev);
    }
}
