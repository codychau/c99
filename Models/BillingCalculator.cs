using System;
using System.Collections.Generic;
using System.Linq;

namespace C99.Models
{
    /// <summary>
    /// AI 底座费用计算（费用始终由累计 Token 总量反算，改价即追溯历史）。
    /// 输入/输出分开计价：单价模式各自乘价；阶梯模式输入/输出各自按自己的累计量独立分档。
    /// </summary>
    public static class BillingCalculator
    {
        /// <summary>计算累计输入/输出 Token 量在给定计费模式下的费用。</summary>
        public static double ComputeBaseCost(
            long promptTokens,
            long completionTokens,
            BillingMode mode,
            double flatInputPricePerMillion,
            double flatOutputPricePerMillion,
            IReadOnlyList<PriceTier> tiers)
        {
            long prompt = Math.Max(0, promptTokens);
            long completion = Math.Max(0, completionTokens);
            if (prompt <= 0 && completion <= 0) return 0;

            if (mode == BillingMode.Tiered && tiers != null && tiers.Count > 0)
            {
                var ordered = tiers
                    .Where(t => t.InputPricePerMillion >= 0 && t.OutputPricePerMillion >= 0)
                    .OrderBy(t => t.MaxTokens > 0 ? t.MaxTokens : long.MaxValue)
                    .ToList();

                // 阶梯为空或全为 0 价（未配置）时回退到单价模式
                if (ordered.Count > 0 && ordered.Any(t => t.InputPricePerMillion > 0 || t.OutputPricePerMillion > 0))
                {
                    return ComputeTieredCost(prompt, ordered, t => t.InputPricePerMillion)
                         + ComputeTieredCost(completion, ordered, t => t.OutputPricePerMillion);
                }
            }

            return prompt * (Math.Max(0, flatInputPricePerMillion) / 1_000_000.0)
                 + completion * (Math.Max(0, flatOutputPricePerMillion) / 1_000_000.0);
        }

        /// <summary>单一方向的阶梯计算：按阈值上限从低到高把累计量分段，各段按该档价格计价</summary>
        private static double ComputeTieredCost(
            long tokens,
            IReadOnlyList<PriceTier> ordered,
            Func<PriceTier, double> priceOf)
        {
            double cost = 0;
            long prevUpper = 0;
            foreach (var tier in ordered)
            {
                if (tokens <= prevUpper) break;

                long upper = tier.MaxTokens > 0 ? tier.MaxTokens : long.MaxValue;
                double bandWidth = upper <= prevUpper ? 0 : (upper - (double)prevUpper);
                double inBand = tokens - prevUpper;
                if (inBand > bandWidth) inBand = bandWidth;

                cost += inBand * (Math.Max(0, priceOf(tier)) / 1_000_000.0);
                prevUpper = upper;
            }
            return cost;
        }
    }
}