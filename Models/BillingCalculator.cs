using System;
using System.Collections.Generic;
using System.Linq;

namespace C99.Models
{
    /// <summary>
    /// AI 底座费用计算（费用始终由累计 Token 总量反算，改价即追溯历史）。
    /// </summary>
    public static class BillingCalculator
    {
        /// <summary>计算累计 Token 总量在给定计费模式下的费用。</summary>
        public static double ComputeBaseCost(
            long totalTokens,
            BillingMode mode,
            double flatPricePerMillion,
            IReadOnlyList<PriceTier> tiers)
        {
            if (totalTokens <= 0) return 0;

            if (mode == BillingMode.Tiered && tiers != null && tiers.Count > 0)
            {
                var ordered = tiers
                    .Where(t => t.PricePerMillion >= 0)
                    .OrderBy(t => t.MaxTokens > 0 ? t.MaxTokens : long.MaxValue)
                    .ToList();

                // 阶梯为空或全为 0 价（未配置）时回退到单价模式
                if (ordered.Count > 0 && ordered.Any(t => t.PricePerMillion > 0))
                {
                    double cost = 0;
                    long prevUpper = 0;
                    foreach (var tier in ordered)
                    {
                        if (totalTokens <= prevUpper) break;

                        long upper = tier.MaxTokens > 0 ? tier.MaxTokens : long.MaxValue;
                        double bandWidth = upper <= prevUpper ? 0 : (upper - (double)prevUpper);
                        double inBand = totalTokens - prevUpper;
                        if (inBand > bandWidth) inBand = bandWidth;

                        cost += inBand * (tier.PricePerMillion / 1_000_000.0);
                        prevUpper = upper;
                    }
                    return cost;
                }
            }

            return totalTokens * (Math.Max(0, flatPricePerMillion) / 1_000_000.0);
        }
    }
}