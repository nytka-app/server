using System.Text;
using Nytka.Server.Webhooks;

namespace Nytka.Server.Tests.Webhooks;

public sealed class WebhookSignerTests
{
    [Fact]
    public void Sign_matches_a_known_vector()
    {
        // Independently computed: printf '1700000000.{"id":"x"}' | openssl dgst -sha256 -hmac 'whsec_test'
        var signature = WebhookSigner.Sign("whsec_test", 1700000000, Encoding.UTF8.GetBytes("""{"id":"x"}"""));

        Assert.Equal("v1=80e8d098018f757aa814ab1abb924f35d304affa1d93d29611a54696207eb7c3", signature);
    }

    [Fact]
    public void Sign_covers_the_timestamp_and_the_exact_bytes()
    {
        var body = Encoding.UTF8.GetBytes("""{"id":"x"}""");
        var signature = WebhookSigner.Sign("whsec_test", 1700000000, body);

        Assert.NotEqual(signature, WebhookSigner.Sign("whsec_test", 1700000001, body));
        Assert.NotEqual(signature, WebhookSigner.Sign("whsec_other", 1700000000, body));
        Assert.NotEqual(signature, WebhookSigner.Sign("whsec_test", 1700000000, Encoding.UTF8.GetBytes("""{"id": "x"}""")));
    }

    [Fact]
    public void A_secret_is_whsec_and_43_base64url_characters()
    {
        var secret = WebhookSigner.GenerateSecret();

        Assert.Matches("^whsec_[A-Za-z0-9_-]{43}$", secret);
        Assert.NotEqual(secret, WebhookSigner.GenerateSecret());
    }
}
