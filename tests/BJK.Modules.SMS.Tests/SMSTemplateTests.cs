using BJK.Modules.SMS.Templates;
using Xunit;

namespace BJK.Modules.SMS.Tests;

public sealed class SMSTemplateTests
{
    [Fact]
    public void SMS_templates_fit_in_one_segment()
    {
        foreach (var message in new[]
        {
            SMSTemplates.SignInCode("+447700900123", "12345678"),
            SMSTemplates.ConfirmMobileCode("+447700900123", "12345678"),
        })
        {
            Assert.Equal("+447700900123", message.To);
            Assert.Contains("12345678", message.Body);
            Assert.True(message.Body.Length <= 160, $"{message.Body.Length} characters");
            Assert.All(message.Body, c => Assert.True(c < 128, $"'{c}' isn't plain GSM text"));
        }
    }
}
