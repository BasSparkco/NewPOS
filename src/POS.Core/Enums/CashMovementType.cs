namespace POS.Core.Enums;

/// <summary>What kind of cash-session ledger entry a <see cref="Entities.CashMovement"/> row records.</summary>
public enum CashMovementType
{
    /// <summary>The opening float counted into the drawer when the session was opened.</summary>
    OpeningFloat = 0,
    /// <summary>A payment received against a sale, in whatever <see cref="PaymentMethod"/> was used.</summary>
    SaleReceipt = 1,
    /// <summary>Money paid back out on a refund.</summary>
    Refund = 2,
    /// <summary>A manual cash addition to the drawer (e.g. a bank drop-off, float top-up).</summary>
    CashIn = 3,
    /// <summary>A manual cash removal from the drawer (e.g. a bank drop, paid-out expense).</summary>
    CashOut = 4
}
