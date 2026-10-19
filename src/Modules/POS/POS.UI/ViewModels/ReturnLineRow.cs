using System.Globalization;
using System.Windows.Input;
using Platform.Presentation.Mvvm;
using POS.Contracts.Models;

namespace POS.UI.ViewModels;

/// <summary>
/// MISS-10b: one line of the sale in the till's return: how many units come back (− and + for a finger, or typed) and whether they go back
/// on the shelf or are written off as damaged (owner's choices 2026-10-19). Never below 0 nor above what is left of the line.
/// </summary>
public sealed class ReturnLineRow : ViewModelBase
{
    private decimal _quantity;
    private string _quantityText = "0";
    private bool _isDamaged;

    public ReturnLineRow(POSReturnableLine line)
    {
        Line = line;
        FewerCommand = Command(() => { Quantity = Math.Max(0m, Math.Ceiling(Quantity) - 1m); return Task.CompletedTask; }, () => Quantity > 0m);
        MoreCommand = Command(() => { Quantity = Math.Min(Line.RemainingQuantity, Math.Floor(Quantity) + 1m); return Task.CompletedTask; }, () => Quantity < Line.RemainingQuantity);
    }

    public POSReturnableLine Line { get; }

    public ICommand FewerCommand { get; }
    public ICommand MoreCommand { get; }

    public string ProductName => Line.ProductName;
    public string SoldText => Line.SoldQuantity.ToString("0.###", CultureInfo.CurrentCulture);
    public string LeftText => Line.RemainingQuantity.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>Nothing is left of the line (it was returned in full before): the line shows "Returned" instead of the controls.</summary>
    public bool IsFullyReturned => Line.RemainingQuantity <= 0m;
    public bool CanReturn => !IsFullyReturned;

    /// <summary>The units coming back (0 = the line is not part of this return).</summary>
    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var clamped = Math.Clamp(value, 0m, Math.Max(0m, Line.RemainingQuantity));
            if (!Set(ref _quantity, clamped)) return;
            _quantityText = clamped.ToString("0.###", CultureInfo.CurrentCulture);
            Raise(nameof(QuantityText));
            Raise(nameof(Refund));
            Raise(nameof(IsValid));
        }
    }

    /// <summary>The units as typed; a number from 0 to what is left becomes <see cref="Quantity"/>, anything else leaves the line invalid.</summary>
    public string QuantityText
    {
        get => _quantityText;
        set
        {
            if (!Set(ref _quantityText, value)) return;
            if (TryParse(value, out var typed) && typed <= Line.RemainingQuantity)
            {
                _quantity = typed;
                Raise(nameof(Quantity));
                Raise(nameof(Refund));
            }
            Raise(nameof(IsValid));
        }
    }

    /// <summary>The typed quantity is a number from 0 to what is left.</summary>
    public bool IsValid => TryParse(QuantityText, out var typed) && typed == Quantity;

    /// <summary>What this line gives back (the rule Sales applies when the return is recorded).</summary>
    public decimal Refund => Line.RefundFor(Quantity);

    /// <summary>Written off as damaged (false = back on the shelf, the default).</summary>
    public bool IsDamaged
    {
        get => _isDamaged;
        set
        {
            if (Set(ref _isDamaged, value)) Raise(nameof(GoesToShelf));
        }
    }

    public bool GoesToShelf
    {
        get => !_isDamaged;
        set => IsDamaged = !value;
    }

    private static bool TryParse(string? text, out decimal value)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) && value >= 0m;
}
