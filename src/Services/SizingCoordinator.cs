using System;
using System.Collections.Generic;
using System.Linq;

namespace RafaelElectrical.Services
{
    public static class SizingCoordinator
    {
        public static List<SizingResult> SizeAll(List<CircuitInput> inputs, DimensioningSettings s,
            out int totalFail, List<CircuitInput> context = null)
        {
            var all = (context ?? inputs).Concat(inputs).Distinct().ToList();
            var selected = new HashSet<CircuitInput>(inputs);
            var feeders = all.Where(i => !string.IsNullOrEmpty(i.FeedsPanelKey ?? i.FeedsPanel))
                .GroupBy(i => i.FeedsPanelKey ?? i.FeedsPanel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var cache = new Dictionary<CircuitInput, SizingResult>();
            var visiting = new HashSet<CircuitInput>();
            SizingResult Resolve(CircuitInput i)
            {
                if (cache.TryGetValue(i, out var previous)) return previous;
                SizingResult Fail(string text) => new SizingResult { Input = i, Status = text };
                if (!visiting.Add(i)) return Fail("Ciclo na alimentação dos quadros");
                i.UpstreamDropPercent = 0;
                i.DropLimitOverride = s.CheckTotalVoltageDrop ? Math.Min(s.MaxVoltageDropPercent, s.TotalVoltageDropPercent) : (double?)null;
                SizingResult result = null;
                if (s.CheckTotalVoltageDrop && feeders.TryGetValue(i.PanelKey ?? i.Panel ?? "", out var parents))
                {
                    if (parents.Count != 1) result = Fail("Alimentação ambígua: mais de um alimentador");
                    else
                    {
                        var parent = Resolve(parents[0]);
                        if (!parent.Ok || parent.LengthM <= 0) result = Fail("Alimentador a montante sem dimensionamento válido e comprimento");
                        else
                        {
                            i.UpstreamDropPercent = parent.TotalDropPercent;
                            i.DropLimitOverride = Math.Min(s.MaxVoltageDropPercent, s.TotalVoltageDropPercent - i.UpstreamDropPercent);
                            if (i.DropLimitOverride <= 0) result = Fail("Queda total esgotada a montante");
                        }
                    }
                }
                if (result == null && !selected.Contains(i) && (!i.ExistingSizingValid || !i.ExistingSectionMm2.HasValue))
                    result = Fail("Alimentador fora da seleção sem dimensionamento aprovado");
                result ??= CircuitSizer.Size(i, s, selected.Contains(i) ? null : i.ExistingSectionMm2);
                if (result.Ok && s.CheckTotalVoltageDrop && result.TotalDropPercent > s.TotalVoltageDropPercent + 1e-9)
                { result.Ok = false; result.Status = "Queda total acima do limite"; }
                visiting.Remove(i);
                return cache[i] = result;
            }
            var results = inputs.Select(Resolve).ToList();
            totalFail = results.Count(r => !r.Ok && (r.Status.Contains("Queda") || r.Status.Contains("Alimentador")));
            return results;
        }
    }
}
