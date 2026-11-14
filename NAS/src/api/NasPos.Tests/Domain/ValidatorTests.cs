using FluentAssertions;
using NasPos.Domain.Entities;
using NasPos.Domain.Validation;

namespace NasPos.Tests.Domain;

public class ValidatorTests
{
    [Theory]
    [InlineData("27AAPFU0939F1ZV", true)]
    [InlineData("27aapfu0939f1zv", true)]
    [InlineData("27AAPFU0939F1ZW", false)]
    [InlineData("27AAPFU0939F1Z", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Gstin_format_and_checksum(string? gstin, bool valid) => Gstin.IsValid(gstin).Should().Be(valid);

    [Theory]
    [InlineData("27AAPFU0939F1Z", "15 characters")]
    [InlineData("27AAPFU0939F1Z*", "letters and digits")]
    [InlineData("00AAPFU0939F1ZV", "state code")]
    [InlineData("27AAPF10939F1ZV", "PAN")]
    [InlineData("27AAPFU0939F1ZW", "check character")]
    public void Gstin_errors_say_what_is_wrong(string gstin, string expected) =>
        Gstin.Error(gstin).Should().Contain(expected);

    [Fact]
    public void Gstin_accepts_spaces_hyphens_and_lowercase() => Gstin.IsValid(" 27 aapfu-0939f1zv ").Should().BeTrue();

    [Fact]
    public void Gstin_accepts_non_regular_registrations_with_valid_check_character()
    {
        // e.g. a TDS registration: state + TAN-style body + 'D' in position 14 (not 'Z').
        string body = "07DELA12345B1D";
        Gstin.IsValid(body + Gstin.CheckChar(body)).Should().BeTrue();
        Gstin.IsValid(body + (Gstin.CheckChar(body) == 'A' ? 'B' : 'A')).Should().BeFalse();
    }

    [Theory]
    [InlineData("4006381333931", true)]   // EAN-13
    [InlineData("4006381333932", false)]
    [InlineData("036000291452", true)]    // UPC-A
    [InlineData("73513537", true)]        // EAN-8
    [InlineData("73513538", false)]
    [InlineData("ABC-123", true)]         // not a GS1 number: no check digit to verify
    [InlineData("12345", true)]
    public void Ean_check_digit(string code, bool valid) => Ean.HasValidCheck(code).Should().Be(valid);

    [Fact]
    public void Ean_check_digit_for_generated_in_store_code() => Ean.CheckDigit("200000000001").Should().Be('5');

    [Fact]
    public void Scale_barcode_weight()
    {
        var f = ScaleBarcodeFormat.Parse("$|5|5|1000");
        var r = ScaleBarcodeParser.TryParse("$00123001250", f);
        r.Should().NotBeNull();
        r!.ItemCode.Should().Be("00123");
        r.IsPrice.Should().BeFalse();
        r.Value.Should().Be(1.25m);
    }

    [Fact]
    public void Scale_barcode_price()
    {
        var f = ScaleBarcodeFormat.Parse("21|4|5|1000");
        var r = ScaleBarcodeParser.TryParse("21ABCD100450", f);
        r!.ItemCode.Should().Be("ABCD");
        r.IsPrice.Should().BeTrue();
        r.Value.Should().Be(450);
    }

    [Fact]
    public void Scale_barcode_rejects_other_prefix() =>
        ScaleBarcodeParser.TryParse("99ABCD100450", ScaleBarcodeFormat.Parse("21|4|5|1000")).Should().BeNull();

    [Fact]
    public void Offer_window_is_inclusive_and_ordered()
    {
        var now = DateTimeOffset.Parse("2026-05-10T10:00:00+05:30");
        var o = new ItemOffer { StartAt = now.AddDays(-1), EndAt = now.AddDays(1) };
        o.IsActiveAt(now).Should().BeTrue();
        o.IsActiveAt(now.AddDays(2)).Should().BeFalse();
        o.IsActiveAt(now.AddDays(-2)).Should().BeFalse();
    }
}
