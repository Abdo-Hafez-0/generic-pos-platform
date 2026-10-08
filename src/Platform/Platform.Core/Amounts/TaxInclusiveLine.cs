namespace Platform.Core.Amounts;

/// <summary>
/// The amounts of one sale line when prices INCLUDE tax (FIX-08, user decisions: VAT-style prices; rounding per line to 2 decimals, half
/// away from zero). The till (POS) and the sale (Sales) both compute their lines with this one rule, so the cart the cashier sees, the
/// payment taken and the sale recorded can never differ by a cent.
///
///   Gross    = unit price x quantity                    (rounded)
///   Discount = the discount given on the line           (rounded; never more than Gross)
///   Total    = Gross - Discount                         what the customer pays for the line, tax included
///   Tax      = Total x rate / (1 + rate)                (rounded) the tax contained in Total
///   Net      = Total - Tax                              the amount before tax
///
/// Totals of a sale are the sums of its rounded lines, so receipt lines always add up to the receipt total.
/// </summary>
public readonly record struct TaxInclusiveLine(decimal Gross, decimal Discount, decimal Total, decimal Tax, decimal Net)
{
    public const int Decimals = 2;

    public static decimal Round(decimal amount) => decimal.Round(amount, Decimals, MidpointRounding.AwayFromZero);

    /// <param name="unitPrice">Price of one unit, tax included.</param>
    /// <param name="quantity">Quantity sold.</param>
    /// <param name="discount">Discount on the whole line, tax included (0 = none). Callers validate it; here it is capped at Gross.</param>
    /// <param name="taxRate">Rate as a fraction (0.14 = 14%); 0 = no tax.</param>
    public static TaxInclusiveLine Compute(decimal unitPrice, decimal quantity, decimal discount, decimal taxRate)
    {
        var gross = Round(unitPrice * quantity);
        var given = Math.Min(Round(Math.Max(discount, 0m)), gross);
        var total = gross - given;
        var tax = taxRate <= 0m ? 0m : Round(total * taxRate / (1m + taxRate));
        return new TaxInclusiveLine(gross, given, total, tax, total - tax);
    }
}
