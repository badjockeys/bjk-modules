using BJK.Modules.Safety;
using FluentValidation;
using Xunit;

namespace BJK.Modules.Safety.Tests;

public sealed class TextSafetyTests
{
    [Theory]
    [InlineData("Silver car AB12 CDE cut me up")]
    [InlineData("plate was ab12cde")]
    [InlineData("reg Ab12 cDe")]
    [InlineData("A123 BCD undertook me")]
    [InlineData("ABC 123D was tailgating")]
    [InlineData("abc123d")]
    [InlineData("LAZ 1234 in Belfast")]
    [InlineData("NI car axz 99")]
    [InlineData("old plate ABC 123")]
    [InlineData("old plate 1234 AB")]
    public void Number_plates_are_refused(string text)
    {
        Assert.True(TextSafety.ContainsNumberPlate(text));
        Assert.Equal(PersonalDetailKind.NumberPlate, TextSafety.Check(text)?.Kind);
    }

    [Theory]
    [InlineData("M62 J26")]
    [InlineData("A road")]
    [InlineData("the A56 near Bury")]
    [InlineData("the A580 East Lancs Road")]
    [InlineData("doing 70mph in a 30")]
    [InlineData("WAS DOING 90 IN A 30")]
    [InlineData("THE M60 AT J12")]
    [InlineData("at 10pm on the M6")]
    [InlineData("BMW X5 on a 2 lane road")]
    [InlineData("white BMW 320d undertaking")]
    [InlineData("Junction 26 of the M62")]
    [InlineData("the M6 and A6 at 5 KM")]
    [InlineData("Golf GTI MK 7")]
    [InlineData("Range Rover on the A34 was speeding")]
    [InlineData("DOING 50 MPH ON THE A1")]
    [InlineData("VW Golf GT 86")]
    [InlineData("A6 BUS lane")]
    [InlineData("a Kia 2019 model")]
    [InlineData("on the B6222 near Radcliffe")]
    [InlineData("J26 to J27 M62 eastbound")]
    [InlineData("Volvo XC90 on the M60")]
    [InlineData("Lexus RX450h at 80")]
    [InlineData("Fiesta ST 200 tailgating")]
    [InlineData("M25 between J10 and J11")]
    [InlineData("saw the 2 a roads")]
    [InlineData("A1(M) J34")]
    public void Roads_junctions_speeds_and_car_models_are_fine(string text)
    {
        Assert.False(TextSafety.ContainsNumberPlate(text));
        Assert.Null(TextSafety.Check(text));
    }

    [Theory]
    [InlineData("call me on 07700 900123")]
    [InlineData("07700900123")]
    [InlineData("ring 0161 496 0000")]
    [InlineData("+44 (0)161 496 0000")]
    [InlineData("+44 7700 900123")]
    [InlineData("+1 (555) 123-4567")]
    [InlineData("0033 1 23 45 67 89")]
    public void Phone_numbers_are_refused(string text)
    {
        Assert.Equal(PersonalDetailKind.PhoneNumber, TextSafety.Check(text)?.Kind);
    }

    [Theory]
    [InlineData("18/10/2026 at 14:30")]
    [InlineData("between 0800 and 0900")]
    [InlineData("2024-10-18")]
    [InlineData("cost me 2000 quid to fix")]
    [InlineData("call 999")]
    [InlineData("doing 100 in a 50")]
    public void Dates_times_and_amounts_arent_phone_numbers(string text)
    {
        Assert.False(TextSafety.ContainsPhoneNumber(text));
    }

    [Theory]
    [InlineData("email me at rider@example.com")]
    [InlineData("R.Hog+bj@mail.co.uk")]
    public void Email_addresses_are_refused(string text)
    {
        Assert.Equal(PersonalDetailKind.EmailAddress, TextSafety.Check(text)?.Kind);
    }

    [Theory]
    [InlineData("follow @roadhog_99")]
    [InlineData("x.com/roadhog")]
    [InlineData("https://twitter.com/roadhog/status/1")]
    [InlineData("https://www.tiktok.com/@roadhog/video/123")]
    [InlineData("instagram.com/roadhog")]
    [InlineData("facebook.com/some.person")]
    [InlineData("youtube.com/@roadhog")]
    public void Social_handles_and_profile_links_are_refused(string text)
    {
        Assert.Equal(PersonalDetailKind.SocialHandle, TextSafety.Check(text)?.Kind);
    }

    [Theory]
    [InlineData("@ the lights")]
    [InlineData("see tiktok for more")]
    public void Mentioning_a_site_or_at_sign_is_fine(string text)
    {
        Assert.Null(TextSafety.Check(text));
    }

    [Theory]
    [InlineData("lives at 12 Acacia Avenue")]
    [InlineData("221B Baker Street")]
    [InlineData("14 St Mary's Road")]
    [InlineData("car park at 3 Bridge Street")]
    [InlineData("10 old kent road")]
    public void Street_addresses_are_refused(string text)
    {
        Assert.Equal(PersonalDetailKind.StreetAddress, TextSafety.Check(text)?.Kind);
    }

    [Theory]
    [InlineData("3 lane motorway")]
    [InlineData("3 cars on Bury Road")]
    [InlineData("doing 40 down the road")]
    [InlineData("5 idiots blocking Bury Road")]
    [InlineData("2 miles down Station Road")]
    [InlineData("Shell garage on Bury New Road")]
    [InlineData("in lane 3 of 4")]
    public void Road_names_without_a_house_number_are_fine(string text)
    {
        Assert.False(TextSafety.ContainsStreetAddress(text));
    }

    [Fact]
    public void The_plate_message_explains_blurring()
    {
        Assert.Equal(
            "Number plates can't be included. Faces and plates are blurred automatically, so there's no need to describe them.",
            TextSafety.Check("AB12 CDE")!.Message);
    }

    [Fact]
    public void Blank_text_is_fine()
    {
        Assert.Null(TextSafety.Check(null));
        Assert.Null(TextSafety.Check("   "));
    }

    [Fact]
    public void The_validation_rule_reports_the_friendly_message_on_the_field()
    {
        var validator = new InlineValidator<Sample>();
        validator.RuleFor(s => s.Title).NoPersonalDetails();
        validator.RuleFor(s => s.Description).NoPersonalDetails();

        var result = validator.Validate(new Sample("Close pass by AB12 CDE", null));

        var error = Assert.Single(result.Errors);
        Assert.Equal("Title", error.PropertyName);
        Assert.Equal(TextSafety.NumberPlateMessage, error.ErrorMessage);
        Assert.True(validator.Validate(new Sample("Close pass on the M62 J26", "A road near Bury")).IsValid);
    }

    [Theory]
    [InlineData("Cheap insurance at https://example.test/quote")]
    [InlineData("see http://foo")]
    [InlineData("visit www.cheap-cars.example")]
    [InlineData("best deals cheap-insurance.co.uk today")]
    [InlineData("bit.ly/abc123")]
    [InlineData("go to dashcams.shop now")]
    [InlineData("WWW.SPAM.EXAMPLE")]
    public void Web_addresses_are_found(string text) => Assert.True(TextSafety.ContainsWebAddress(text));

    [Theory]
    [InlineData("Turned left.In the wrong lane")]
    [InlineData("St.Helens ring road")]
    [InlineData("Doing 70 in a 30, e.g. near the M62")]
    [InlineData("3.5 tonne van on the A580")]
    [InlineData("left.in the bus lane")]
    [InlineData("Mr.Smith's driving school car")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_driving_text_isnt_a_web_address(string? text) => Assert.False(TextSafety.ContainsWebAddress(text));

    [Fact]
    public void The_link_rule_refuses_with_a_friendly_message()
    {
        var validator = new InlineValidator<Sample>();
        validator.RuleFor(s => s.Title).NoWebAddresses();

        var error = Assert.Single(validator.Validate(new Sample("Get your dashcam at dashcams.shop", null)).Errors);
        Assert.Equal(TextSafety.WebAddressMessage, error.ErrorMessage);
        Assert.Equal("WebAddress", error.ErrorCode);
        Assert.True(validator.Validate(new Sample("Close pass on the A57", null)).IsValid);
    }

    public sealed record Sample(string Title, string? Description);
}
