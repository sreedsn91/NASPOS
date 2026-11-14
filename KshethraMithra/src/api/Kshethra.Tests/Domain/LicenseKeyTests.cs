using System.Security.Cryptography;
using FluentAssertions;
using Kshethra.Application.Licensing;
using Kshethra.Domain;
using Kshethra.Domain.Entities;
using Kshethra.Domain.Licensing;

namespace Kshethra.Tests.Domain;

public class LicenseKeyTests
{
    private static readonly DateOnly Start = new(2026, 10, 5);
    private static IEnumerable<ECDsa> Trusted => LicenseTrust.Trusted;

    [Theory]
    [InlineData(1, "2026-11-04", "1 month")]
    [InlineData(6, "2027-04-04", "6 months")]
    [InlineData(12, "2027-10-04", "1 year")]
    [InlineData(18, "2028-04-04", "1 year 6 months")]
    [InlineData(24, "2028-10-04", "2 years")]
    [InlineData(240, "2046-10-04", "20 years")]
    [InlineData(null, null, "Lifetime")]
    public void Keys_round_trip_with_any_length_in_months(int? months, string? expires, string text)
    {
        var key = TestLicense.Key("Sree Bhagavathy Stores", months, Start);
        key.Should().StartWith("NAS1-");
        var info = LicenseKey.Verify(key, "Sree Bhagavathy Stores", null, Trusted);
        info.Months.Should().Be(months);
        LicenseKey.DurationText(info.Months).Should().Be(text);
        info.ExpiresOn.Should().Be(expires is null ? null : DateOnly.Parse(expires));
        info.KeyId.Should().HaveLength(12);
    }

    [Fact]
    public void Lengths_outside_one_month_to_twenty_years_are_refused()
    {
        ((Action)(() => TestLicense.Key("X", 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => TestLicense.Key("X", 241))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Keys_in_the_first_format_still_work()
    {
        // Version-1 key (lifetime, unlimited users) issued before lengths became months — used by the browser tests.
        const string v1 = "NAS1-044GK-9FZZW-X4FE3-XFHFS-1VSHZ-31KSW-DA000-00000-01PN1-NFYCC-ZYMNC-1K7XX-JK258-NMP01-A65GA-GKV8H-PX8JY-3NSSA-GFWNT-85AHM-7ZCF8-TRQ6M-7R54S-MY9EF-FGBES-NB0FN-HXEMG-N1ZV3-8KEW";
        var info = LicenseKey.Verify(v1, "E2E Stores", null, Trusted, LicenseKey.Products.NasPos);
        info.Months.Should().BeNull();
        info.ExpiresOn.Should().BeNull();
        info.MaxUsers.Should().BeNull();
    }

    [Fact]
    public void A_key_for_the_shop_product_does_not_unlock_the_temple_app()
    {
        using var k = TestLicense.SigningKey();
        var shopKey = LicenseKey.Create(k, "Sree Krishna Temple", null, 12, Start, Start, null, LicenseKey.Products.NasPos);
        var act = () => LicenseKey.Verify(shopKey, "Sree Krishna Temple", null, Trusted);
        act.Should().Throw<LicenseKeyException>().Where(e => e.Code == "license_company_mismatch");
        LicenseKey.Verify(TestLicense.Key("Sree Krishna Temple", 12, Start), "Sree Krishna Temple", null, Trusted).Months.Should().Be(12);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    [InlineData(9999)]
    [InlineData(null)]
    public void Keys_carry_the_user_limit(int? users)
    {
        var key = TestLicense.Key("Sree Stores", 12, Start, maxUsers: users);
        LicenseKey.Verify(key, "Sree Stores", null, Trusted).MaxUsers.Should().Be(users);
    }

    [Fact]
    public void Name_match_ignores_case_spaces_and_punctuation()
    {
        var key = TestLicense.Key("Sree Bhagavathy Stores, Kochi");
        LicenseKey.Verify(key.ToLowerInvariant(), "SREE BHAGAVATHY STORES KOCHI", null, Trusted).Should().NotBeNull();
    }

    [Fact]
    public void Key_for_another_company_is_rejected()
    {
        var key = TestLicense.Key("Sree Bhagavathy Stores");
        var act = () => LicenseKey.Verify(key, "Metro Wholesale", null, Trusted);
        act.Should().Throw<LicenseKeyException>().Where(e => e.Code == "license_company_mismatch");
    }

    [Fact]
    public void Gstin_bound_key_needs_the_same_gstin()
    {
        var key = TestLicense.Key("Sree Stores", gstin: "32ABCDE1234F1Z5");
        LicenseKey.Verify(key, "Sree Stores", "32abcde1234f1z5", Trusted).BoundToGstin.Should().BeTrue();
        var act = () => LicenseKey.Verify(key, "Sree Stores", "33ABCDE1234F1Z5", Trusted);
        act.Should().Throw<LicenseKeyException>().Where(e => e.Code == "license_gstin_mismatch");
    }

    [Fact]
    public void Edited_or_forged_keys_fail_the_signature()
    {
        var key = TestLicense.Key("Sree Stores", 12, Start);
        var chars = key.ToCharArray();
        var i = key.Length / 2;
        chars[i] = chars[i] == 'A' ? 'B' : 'A';
        ((Action)(() => LicenseKey.Verify(new string(chars), "Sree Stores", null, Trusted))).Should().Throw<LicenseKeyException>();

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = LicenseKey.Create(stranger, "Sree Stores", null, null, Start, Start, null);
        ((Action)(() => LicenseKey.Verify(forged, "Sree Stores", null, Trusted))).Should().Throw<LicenseKeyException>();

        ((Action)(() => LicenseKey.Verify(key[..^10], "Sree Stores", null, Trusted))).Should().Throw<LicenseKeyException>();
    }

    [Fact]
    public void Status_follows_expiry_grace_and_warning_window()
    {
        var c = new Company { Name = "X" };
        LicenseService.StatusOf(c, Start).State.Should().Be("Missing");
        c.LicenseGraceUntil = Start.AddDays(10);
        LicenseService.StatusOf(c, Start).Should().Match<LicenseStatus>(s => s.State == "Grace" && s.CanBill && s.DaysLeft == 10);
        LicenseService.StatusOf(c, Start.AddDays(11)).CanBill.Should().BeFalse();

        var info = LicenseKey.Verify(TestLicense.Key("X", 12, Start), "X", null, Trusted);
        LicenseService.Apply(c, info, TestLicense.Key("X", 12, Start), DateTimeOffset.Now);
        LicenseService.StatusOf(c, Start).State.Should().Be("Active");
        LicenseService.StatusOf(c, new DateOnly(2027, 9, 20)).State.Should().Be("ExpiringSoon");
        LicenseService.StatusOf(c, new DateOnly(2027, 10, 4)).CanBill.Should().BeTrue("the last day is still valid");
        LicenseService.StatusOf(c, new DateOnly(2027, 10, 5)).Should().Match<LicenseStatus>(s => s.State == "Expired" && !s.CanBill);
    }

    [Fact]
    public void Status_and_user_limit_come_from_the_key_not_the_stored_copy()
    {
        var key = TestLicense.Key("X", 12, Start, maxUsers: 3);
        var c = new Company { Name = "X" };
        LicenseService.Apply(c, LicenseKey.Verify(key, "X", null, Trusted), key, DateTimeOffset.Now);
        c.LicenseExpiresOn = null; c.LicenseMonths = null; c.LicenseMaxUsers = null; // edited in the database
        LicenseService.StatusOf(c, new DateOnly(2027, 10, 5)).Should().Match<LicenseStatus>(s => s.State == "Expired" && !s.CanBill && s.Plan == "1 year");
        LicenseService.StoredKey(c)!.MaxUsers.Should().Be(3);
    }

    [Fact]
    public void A_stored_key_that_no_longer_verifies_blocks_billing()
    {
        var key = TestLicense.Key("X", null, Start);
        var c = new Company { Name = "X" };
        LicenseService.Apply(c, LicenseKey.Verify(key, "X", null, Trusted), key, DateTimeOffset.Now);
        LicenseService.StatusOf(c, Start).State.Should().Be("Active");

        c.Name = "Y"; // renamed in the database
        LicenseService.StatusOf(c, Start).Should().Match<LicenseStatus>(s => s.State == "Invalid" && !s.CanBill);
        c.Name = "x.";
        LicenseService.StatusOf(c, Start).State.Should().Be("Active", "capitals and punctuation don't matter");

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        c.LicenseKey = LicenseKey.Create(stranger, "X", null, null, Start, Start, null); // a key signed by someone else
        LicenseService.StatusOf(c, Start).State.Should().Be("Invalid");
    }

    [Fact]
    public void Check_refuses_an_expired_key_with_a_clear_message()
    {
        var old = TestLicense.Key("X", 12, new DateOnly(2024, 1, 1));
        var act = () => LicenseService.Check(old, "X", null, Start);
        act.Should().Throw<DomainException>().Where(e => e.Code == "license_expired" && e.Message.Contains("31-12-2024"));
    }
}
