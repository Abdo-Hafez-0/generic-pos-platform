using Client.Hardware.Printing;
using Platform.Application.Abstractions.Hardware;

namespace Hardware.Tests;

/// <summary>
/// MISS-11d-2 (owner's choices, drawn option A): the Saudi simplified invoice on paper - the title in Arabic and English and the VAT number
/// under the shop's name, every label in Arabic and English, the invoice's own totals (excluding VAT, the VAT, including VAT) with the
/// halala of rounding when what was charged differs, and nothing under the QR. Receipts without these marks are unchanged.
/// </summary>
public sealed class SaudiReceiptLayoutTests
{
    private const string Qr = "AQ1TaG9wAg8zMDAwMDAwMDAwMDAwMDM=";
    private static readonly string[] Top = ["فاتورة ضريبية مبسطة", "Simplified Tax Invoice", "الرقم الضريبي VAT No. 300000000000003"];

    private static ReceiptFiscal Saudi(decimal excluding = 1.74m, decimal vat = 0.26m, decimal including = 2.00m)
        => new(Qr, "", "") { TopLines = Top, VatTotals = new ReceiptVatTotals(excluding, vat, including), BilingualLabels = true };

    private static ReceiptDocument Receipt(ReceiptFiscal? fiscal, decimal total = 2.00m)
        => new("Matjar Demo Shop", ["Riyadh"], "S-D93E4979", new DateTimeOffset(2026, 10, 22, 14, 18, 0, TimeSpan.FromHours(3)), "owner",
            [new ReceiptLine("Water 500ml", 2m, 1m, total)], total, null, ["Thank you!"], [new ReceiptTax(0.15m, 0.26m)],
            [new ReceiptPayment("Cash", 1.50m, 2m, 0.50m), new ReceiptPayment("Card (Mada)", total - 1.50m)], Fiscal: fiscal);

    private static List<string> Texts(IReadOnlyList<ReceiptImageLine> lines) => lines.Select(l => l.Right.Length > 0 ? $"{l.Left} | {l.Right}" : l.Left).ToList();

    [Fact]
    public void The_title_and_VAT_number_are_on_top_and_every_label_is_in_Arabic_and_English()
    {
        var lines = EscPosReceiptFormatter.Layout(Receipt(Saudi()), 48);
        var texts = Texts(lines);

        Assert.Equal(["Matjar Demo Shop", "Riyadh", .. Top], texts.Take(5));
        Assert.All(lines.Skip(2).Take(3), l => Assert.True(l.Bold && l.Center));
        Assert.Contains("Invoice رقم الفاتورة | S-D93E4979", texts);
        Assert.Contains("Cashier الكاشير | owner", texts);
        Assert.Contains("Total excl. VAT الإجمالي بدون الضريبة | 1.74", texts);
        Assert.Contains("VAT 15% ضريبة القيمة المضافة | 0.26", texts);
        Assert.Contains("Total incl. VAT الإجمالي شامل الضريبة | 2.00", texts);
        Assert.True(lines.Single(l => l.Left.StartsWith("Total incl. VAT", StringComparison.Ordinal)).Bold);
        Assert.Contains("Cash نقدًا | 1.50", texts);
        Assert.Contains("Card بطاقة (Mada) | 0.50", texts);
        Assert.Contains("Tendered المدفوع | 2.00", texts);
        Assert.Contains("Change الباقي | 0.50", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("TOTAL", StringComparison.Ordinal) || t.StartsWith("incl. tax", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("Rounding", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_printed_under_the_QR()
    {
        var lines = EscPosReceiptFormatter.Layout(Receipt(Saudi()), 48);
        var qr = lines.ToList().FindIndex(l => l.QrPayload == Qr);

        Assert.True(qr > 0);
        Assert.Equal("Thank you!", lines[qr + 1].Left);
    }

    [Fact]
    public void A_halala_between_the_invoice_and_what_was_charged_is_shown_as_rounding()
    {
        // ZATCA: each category's VAT from its taxable amount; 4.34 by the invoice, 4.33 charged
        var texts = Texts(EscPosReceiptFormatter.Layout(Receipt(Saudi(3.77m, 0.57m, 4.34m), total: 4.33m), 48));

        Assert.Contains("Total incl. VAT الإجمالي شامل الضريبة | 4.34", texts);
        Assert.Contains("Rounding التقريب | -0.01", texts);
        Assert.Contains("TOTAL الإجمالي | 4.33", texts);
    }

    [Fact]
    public void A_credit_note_shows_its_return_lines_and_negative_totals_in_both_languages()
    {
        var receipt = Receipt(Saudi() with { TopLines = ["إشعار دائن مبسط", "Simplified Credit Note"] }) with
        {
            Reference = "R-5E6F7A8B", Return = new ReceiptReturn("S-D93E4979", "Wrong size"),
            Lines = [new ReceiptLine("Water 500ml", 2m, 1m, 2m, WrittenOff: true)], Payments = [new ReceiptPayment("Cash", 2m)],
        };
        var texts = Texts(EscPosReceiptFormatter.Layout(receipt, 48));

        Assert.Contains("*** RETURN إرجاع ***", texts);
        Assert.Contains("Return رقم الإرجاع | R-5E6F7A8B", texts);
        Assert.Contains("Of invoice للفاتورة | S-D93E4979", texts);
        Assert.Contains("Water 500ml  (damaged) (تالف)", texts);
        Assert.Contains("Total incl. VAT الإجمالي شامل الضريبة | -2.00", texts);
        Assert.Contains("Reason: السبب Wrong size", texts);
        Assert.Contains("Customer signature توقيع العميل", texts);
    }

    [Fact]
    public void A_receipt_without_the_Saudi_marks_is_unchanged()
    {
        var texts = Texts(EscPosReceiptFormatter.Layout(Receipt(new ReceiptFiscal(Qr, "إيصال إلكتروني", "UUID 1234...5678")), 48));

        Assert.Contains("Receipt | S-D93E4979", texts);
        Assert.Contains("TOTAL | 2.00", texts);
        Assert.Contains("incl. tax 15% | 0.26", texts);
        Assert.Contains("Cash | 1.50", texts);
        Assert.Contains("إيصال إلكتروني", texts);
        Assert.DoesNotContain(texts, t => t.Contains("VAT", StringComparison.Ordinal));
    }
}
