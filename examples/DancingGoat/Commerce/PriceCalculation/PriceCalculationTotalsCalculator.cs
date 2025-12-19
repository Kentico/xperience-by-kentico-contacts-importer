using System.Linq;

namespace DancingGoat.Commerce;

/// <summary>
/// Calculator for getting totals from <see cref="DancingGoatPriceCalculationResult"/>.
/// </summary>
public sealed class PriceCalculationTotalsCalculator
{
    /// <summary>
    /// Gets the total discount amount from the calculation result.
    /// </summary>
    /// <param name="calculationResult">The calculation result.</param>
    public static decimal GetTotalDiscountAmount(DancingGoatPriceCalculationResult calculationResult)
    {
        var catalogDiscounts = calculationResult.Items.Sum(item => item.PromotionData.CatalogPromotionCandidates.FirstOrDefault(x => x.Applied)?.PromotionCandidate.UnitPriceDiscountAmount ?? 0);
        var orderDiscounts = calculationResult.PromotionData.OrderPromotionCandidates.FirstOrDefault(x => x.Applied)?.PromotionCandidate.OrderDiscountAmount ?? 0;

        return catalogDiscounts + orderDiscounts;
    }


    /// <summary>
    /// Gets the subtotal from the calculation result.
    /// </summary>
    /// <param name="calculationResult">The calculation result.</param>
    public static decimal GetSubtotal(DancingGoatPriceCalculationResult calculationResult)
    {
        return calculationResult.Items.Sum(x => x.Quantity * x.ProductData.UnitPrice);
    }


    /// <summary>
    /// Gets the total without shipping and tax from the calculation result.
    /// </summary>
    /// <param name="calculationResult">The calculation result.</param>
    public static decimal GetTotalWithoutShippingAndTax(DancingGoatPriceCalculationResult calculationResult)
    {
        return calculationResult.Items.Sum(x => x.LineSubtotalAfterAllDiscounts);
    }
}
