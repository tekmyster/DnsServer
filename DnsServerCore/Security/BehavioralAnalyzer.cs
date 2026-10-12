using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DnsServerCore.Security
{
    public sealed class PublicSuffixes
    {
        readonly HashSet<string> _rules = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _wildcards = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _exceptions = new HashSet<string>(StringComparer.Ordinal);
        public PublicSuffixes(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string rule = raw.Trim();
                if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal)) continue;
                if (rule.StartsWith('!')) _exceptions.Add(Normalize(rule[1..]));
                else if (rule.StartsWith("*.", StringComparison.Ordinal)) _wildcards.Add(Normalize(rule[2..]));
                else _rules.Add(Normalize(rule));
            }
        }
        public static string Normalize(string name)
        {
            name = name.TrimEnd('.').ToLowerInvariant();
            if (name.Length == 0 || name.Length > 253 || name.Split('.').Any(l => l.Length == 0 || l.Length > 63)) throw new ArgumentException("Invalid DNS name.");
            return new IdnMapping().GetAscii(name).ToLowerInvariant();
        }
        public string RegistrableDomain(string name)
        {
            string[] labels = Normalize(name).Split('.');
            int suffixLength = 1;
            for (int i = 0; i < labels.Length; i++)
            {
                string suffix = string.Join('.', labels, i, labels.Length - i);
                if (_exceptions.Contains(suffix)) { suffixLength = labels.Length - i - 1; break; }
                if (_rules.Contains(suffix)) suffixLength = Math.Max(suffixLength, labels.Length - i);
                if (i > 0 && _wildcards.Contains(suffix)) suffixLength = Math.Max(suffixLength, labels.Length - i + 1);
            }
            return string.Join('.', labels.Skip(Math.Max(0, labels.Length - suffixLength - 1)));
        }
    }

    public sealed class BehavioralAnalyzer
    {
        sealed class Window
        {
            public DateTime Start;
            public int Count, LongLabels, HighEntropy, Txt, Nx;
            public readonly HashSet<string> Unique = new HashSet<string>(StringComparer.Ordinal);
        }
        readonly object _sync = new object();
        readonly Dictionary<string, Window> _windows = new Dictionary<string, Window>(StringComparer.Ordinal);
        readonly PublicSuffixes _suffixes;
        public BehavioralAnalyzer(PublicSuffixes suffixes) { _suffixes = suffixes; }
        public static double Entropy(string text)
        {
            if (text.Length == 0) return 0;
            return text.GroupBy(c => c).Sum(g => { double p = (double)g.Count() / text.Length; return -p * Math.Log2(p); });
        }
        public BehavioralScore Observe(string clientKey, string name, string type, bool nxDomain, DateTime now)
        {
            name = PublicSuffixes.Normalize(name);
            string domain = _suffixes.RegistrableDomain(name);
            string prefix = name.Length > domain.Length ? name[..(name.Length - domain.Length - 1)] : "";
            string longest = prefix.Split('.').OrderByDescending(s => s.Length).First();
            string key = clientKey + "|" + domain;
            lock (_sync)
            {
                if (!_windows.TryGetValue(key, out Window window) || now - window.Start >= TimeSpan.FromMinutes(5) || now < window.Start)
                {
                    if (_windows.Count >= 4096)
                    {
                        foreach (string stale in _windows.Where(p => now - p.Value.Start >= TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray()) _windows.Remove(stale);
                        if (_windows.Count >= 4096) _windows.Remove(_windows.MinBy(p => p.Value.Start).Key);
                    }
                    window = new Window { Start = now }; _windows[key] = window;
                }
                window.Count = Math.Min(1000000, window.Count + 1);
                if (longest.Length >= 32) window.LongLabels++;
                if (longest.Length >= 20 && Entropy(longest) >= 4.0) window.HighEntropy++;
                if (type == "TXT") window.Txt++;
                if (nxDomain) window.Nx++;
                if (prefix.Length > 0 && window.Unique.Count < 256) window.Unique.Add(prefix);
                List<ScoreContribution> points = [];
                if (window.Count >= 30)
                {
                    if ((double)window.LongLabels / window.Count >= .5) points.Add(new("LongLabels", 20, "At least half of sampled names have labels of 32 or more characters."));
                    if ((double)window.HighEntropy / window.Count >= .5) points.Add(new("HighEntropy", 20, "At least half of sampled names have long, high-entropy labels."));
                    if (window.Count <= 256 && window.Unique.Count >= 25 && window.Unique.Count >= window.Count * .8) points.Add(new("UniqueSubdomains", 25, "At least 25 distinct subdomains with a high novelty ratio."));
                    else if (window.Unique.Count == 256) points.Add(new("HighDistinctCount", 20, "At least 256 distinct subdomains; the capped set cannot estimate the full novelty ratio."));
                    if (window.Count >= 60) points.Add(new("QueryVolume", 15, "At least 60 queries in the five-minute observation window."));
                    if ((double)window.Txt / window.Count >= .5) points.Add(new("TxtRatio", 10, "At least half of queries use TXT."));
                    if ((double)window.Nx / window.Count >= .5) points.Add(new("NxDomainRatio", 10, "At least half of responses are NXDOMAIN."));
                }
                return new BehavioralScore(Math.Min(100, points.Sum(p => p.Points)), domain, window.Count, window.Start, points.ToArray());
            }
        }
    }
}
