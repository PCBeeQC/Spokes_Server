namespace Spokes_Server.Core.Models.Core;

using System.Text.Json.Serialization;

/// <summary>
/// Represents party contact information (business name, address, and primary contact person)
/// for clients, suppliers, and internal/external organizations.
/// </summary>
public class PartyContactInfo
{
    public string BusinessName { get; set; } = string.Empty;
    public string ContactPersonPrefix { get; set; } = string.Empty;
    public string ContactPersonName { get; set; } = string.Empty;
    public string ContactPersonTitle { get; set; } = string.Empty;
    public string ContactPersonEmail { get; set; } = string.Empty;
    public string ContactPersonPhone { get; set; } = string.Empty;
    public string BusinessAddressNumber { get; set; } = string.Empty;
    public string BusinessAddressStreet { get; set; } = string.Empty;
    public string BusinessAddressCity { get; set; } = string.Empty;
    public string BusinessAddressState { get; set; } = string.Empty;
    public string BusinessAddressCountry { get; set; } = string.Empty;
    public string BusinessAddressZip { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;

    // Sage 50 Canadian Edition Integration & Baseline Mapping
    public string SageReferenceName { get; set; } = string.Empty;
    public string DefaultSageItemCode { get; set; } = string.Empty;
    public string DefaultGlAccountCode { get; set; } = string.Empty;

    /// <summary>
    /// Combines <see cref="BusinessAddressNumber"/> and <see cref="BusinessAddressStreet"/> cleanly without duplicating civic numbers.
    /// </summary>
    public string GetFormattedStreetAddress()
    {
        var num = BusinessAddressNumber?.Trim() ?? string.Empty;
        var street = BusinessAddressStreet?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(num) && !string.IsNullOrEmpty(street))
        {
            return street.StartsWith(num, System.StringComparison.OrdinalIgnoreCase) ? street : $"{num} {street}";
        }
        return !string.IsNullOrEmpty(street) ? street : num;
    }

    // Backward compatibility for legacy JSON files and obsolete call sites
    [Obsolete("Use BusinessAddressNumber instead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BusinessAdressNumber
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(BusinessAddressNumber)) BusinessAddressNumber = value; }
    }

    [Obsolete("Use BusinessAddressStreet instead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BusinessAdressStreet
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(BusinessAddressStreet)) BusinessAddressStreet = value; }
    }

    [Obsolete("Use BusinessAddressCity instead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BusinessAdressCity
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(BusinessAddressCity)) BusinessAddressCity = value; }
    }

    [Obsolete("Use BusinessAddressState instead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BusinessAdressState
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(BusinessAddressState)) BusinessAddressState = value; }
    }

    [Obsolete("Use BusinessAddressCountry instead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BusinessAdressCountry
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(BusinessAddressCountry)) BusinessAddressCountry = value; }
    }

    [Obsolete("Use BusinessAddressZip instead")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BusinessAdressZip
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value) && string.IsNullOrEmpty(BusinessAddressZip)) BusinessAddressZip = value; }
    }
}
