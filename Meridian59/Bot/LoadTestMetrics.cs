/*
 Copyright (c) 2012-2013 Clint Banzhaf
 This file is part of "Meridian59 .NET".

 "Meridian59 .NET" is free software:
 You can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation,
 either version 3 of the License, or (at your option) any later version.

 "Meridian59 .NET" is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
 without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 See the GNU General Public License for more details.

 You should have received a copy of the GNU General Public License along with "Meridian59 .NET".
 If not, see http://www.gnu.org/licenses/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Meridian59.Bot
{
    /// <summary>
    /// Collects named numeric samples and renders ASCII histograms and line graphs.
    /// Thread-safe.
    /// </summary>
    public class LoadTestMetrics
    {
        private class Sample
        {
            public double ValueMs;
            public long ElapsedMs;
        }

        private readonly Dictionary<string, List<Sample>> series =
            new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> seriesStart =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object lockObj = new object();

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Records a latency sample in milliseconds for the named metric.
        /// </summary>
        public void Record(string name, double valueMs)
        {
            lock (lockObj)
            {
                if (!series.ContainsKey(name))
                {
                    series[name] = new List<Sample>();
                    seriesStart[name] = NowMs();
                }
                series[name].Add(new Sample
                {
                    ValueMs = valueMs,
                    ElapsedMs = NowMs() - seriesStart[name]
                });
            }
        }

        /// <summary>
        /// Returns all tracked metric names.
        /// </summary>
        public List<string> GetNames()
        {
            lock (lockObj)
                return new List<string>(series.Keys);
        }

        /// <summary>
        /// Returns a single-line stats summary: count, min, mean, p50, p95, p99, max.
        /// </summary>
        public string GetStats(string name)
        {
            lock (lockObj)
            {
                List<Sample> data;
                if (!series.TryGetValue(name, out data) || data.Count == 0)
                    return name + ": no data";
                return BuildStats(name, data);
            }
        }

        /// <summary>
        /// Renders an ASCII histogram. Returns multi-line string.
        /// </summary>
        public string RenderHistogram(string name, int buckets = 10, int barWidth = 30)
        {
            lock (lockObj)
            {
                List<Sample> data;
                if (!series.TryGetValue(name, out data) || data.Count == 0)
                    return name + ": no data";
                return BuildHistogram(name, data, buckets, barWidth);
            }
        }

        /// <summary>
        /// Renders an ASCII line graph of values over time. Returns multi-line string.
        /// </summary>
        public string RenderTimeSeries(string name, int width = 60, int height = 10)
        {
            lock (lockObj)
            {
                List<Sample> data;
                if (!series.TryGetValue(name, out data) || data.Count == 0)
                    return name + ": no data";
                return BuildTimeSeries(name, data, width, height);
            }
        }

        /// <summary>
        /// Saves a full report (stats + histogram + time series + raw CSV) for all metrics to a file.
        /// </summary>
        public void SaveToFile(string path)
        {
            // Snapshot under lock so we don't hold the lock during file I/O
            Dictionary<string, List<Sample>> snapshot;
            lock (lockObj)
            {
                snapshot = new Dictionary<string, List<Sample>>(StringComparer.Ordinal);
                foreach (var kvp in series)
                    snapshot[kvp.Key] = new List<Sample>(kvp.Value);
            }

            using (var sw = new StreamWriter(path, false, Encoding.UTF8))
            {
                sw.WriteLine("=== Meridian59 Load Test Metrics ===");
                sw.WriteLine("Saved: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sw.WriteLine();

                foreach (var kvp in snapshot)
                {
                    string name = kvp.Key;
                    List<Sample> data = kvp.Value;

                    sw.WriteLine(new string('=', 60));
                    sw.WriteLine("METRIC: " + name);
                    sw.WriteLine(new string('=', 60));
                    sw.WriteLine();

                    sw.WriteLine(BuildStats(name, data));
                    sw.WriteLine();

                    sw.WriteLine(BuildHistogram(name, data, buckets: 12, barWidth: 40));
                    sw.WriteLine();

                    sw.WriteLine(BuildTimeSeries(name, data, width: 72, height: 12));
                    sw.WriteLine();

                    sw.WriteLine("--- raw data (elapsed_ms,value_ms) ---");
                    foreach (var s in data)
                        sw.WriteLine(s.ElapsedMs + "," + s.ValueMs.ToString("F3"));

                    sw.WriteLine();
                }
            }
        }

        // -----------------------------------------------------------------------
        // Private rendering — callers must hold lockObj OR pass a safe snapshot
        // -----------------------------------------------------------------------

        private static string BuildStats(string name, List<Sample> data)
        {
            var vals = new List<double>(data.Count);
            foreach (var s in data) vals.Add(s.ValueMs);
            vals.Sort();

            int n = vals.Count;
            double sum = 0;
            foreach (var v in vals) sum += v;

            return string.Format(
                "{0}: n={1} min={2:F0} mean={3:F0} p50={4:F0} p95={5:F0} p99={6:F0} max={7:F0} ms",
                name, n,
                vals[0],
                sum / n,
                vals[n / 2],
                vals[Math.Min(n - 1, (int)(n * 0.95))],
                vals[Math.Min(n - 1, (int)(n * 0.99))],
                vals[n - 1]);
        }

        private static string BuildHistogram(string name, List<Sample> data, int buckets, int barWidth)
        {
            var vals = new List<double>(data.Count);
            foreach (var s in data) vals.Add(s.ValueMs);

            int n = vals.Count;
            double min = double.MaxValue, max = double.MinValue;
            foreach (var v in vals)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            double range = max - min;
            if (range == 0) range = 1;
            double bucketSize = range / buckets;

            int[] counts = new int[buckets];
            foreach (var v in vals)
            {
                int b = (int)((v - min) / bucketSize);
                if (b >= buckets) b = buckets - 1;
                counts[b]++;
            }

            int maxCount = 0;
            foreach (var c in counts)
                if (c > maxCount) maxCount = c;

            var sb = new StringBuilder();
            sb.AppendLine("=== " + name + " histogram (n=" + n + ") ===");

            for (int i = 0; i < buckets; i++)
            {
                double lo = min + i * bucketSize;
                double hi = lo + bucketSize;
                int barLen = maxCount > 0 ? (int)((double)counts[i] / maxCount * barWidth) : 0;
                string bar = new string('\u2588', barLen);
                sb.AppendLine(string.Format(
                    "{0,6:F0}-{1,-6:F0} |{2,-" + barWidth + "}| {3,4}  {4,3:F0}%",
                    lo, hi, bar, counts[i], 100.0 * counts[i] / n));
            }

            sb.Append(BuildStats(name, data));
            return sb.ToString();
        }

        private static string BuildTimeSeries(string name, List<Sample> data, int width, int height)
        {
            int n = data.Count;

            double[] yVals = new double[width];
            long[] xMs = new long[width];
            for (int col = 0; col < width; col++)
            {
                int idx = n == 1 ? 0 : (int)((double)col / (width - 1) * (n - 1));
                yVals[col] = data[idx].ValueMs;
                xMs[col] = data[idx].ElapsedMs;
            }

            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (var y in yVals)
            {
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
            double yRange = yMax - yMin;
            if (yRange == 0) yRange = 1;

            char[,] grid = new char[height, width];
            for (int r = 0; r < height; r++)
                for (int c = 0; c < width; c++)
                    grid[r, c] = ' ';

            for (int col = 0; col < width; col++)
            {
                int row = height - 1 - (int)((yVals[col] - yMin) / yRange * (height - 1));
                if (row < 0) row = 0;
                if (row >= height) row = height - 1;
                grid[row, col] = '*';
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== " + name + " over time (n=" + n + ") ===");

            for (int r = 0; r < height; r++)
            {
                double yLabel = yMax - (double)r / (height - 1) * yRange;
                sb.Append(string.Format("{0,6:F0} |", yLabel));
                for (int c = 0; c < width; c++)
                    sb.Append(grid[r, c]);
                sb.AppendLine();
            }

            sb.Append("       +");
            sb.AppendLine(new string('-', width));

            // X axis labels
            sb.Append("        ");
            long totalMs = xMs[width - 1];
            int tickCount = Math.Min(5, width / 10);
            int prevEnd = 0;
            for (int t = 0; t <= tickCount; t++)
            {
                int pos = (int)((double)t / tickCount * (width - 1));
                int pad = pos - prevEnd;
                if (pad > 0)
                    sb.Append(new string(' ', pad));
                long ms = (long)((double)t / tickCount * totalMs);
                string lbl = ms < 10000 ? ms + "ms" : (ms / 1000) + "s";
                sb.Append(lbl);
                prevEnd = pos + lbl.Length;
            }

            sb.AppendLine();
            sb.Append(BuildStats(name, data));
            return sb.ToString();
        }
    }
}
