using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Client.Fiscal.Http;
using Fiscal.Contracts.Models;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11c-3: the client's HTTPS transport carries a part's call as asked (method, address, headers, a bearer token, a form or JSON body) and
/// gives the answer back as it came; a network problem or a non-HTTPS address is an answer with status 0, never an exception.
/// </summary>
public sealed class HttpFiscalTransportTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer(request);
        }
    }

    [Fact]
    public async Task A_form_request_goes_with_its_headers_and_the_answer_comes_back_as_it_came()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t\"}") });
        var transport = new HttpFiscalTransport(new HttpClient(handler));

        var answer = await transport.SendAsync(new FiscalHttpRequest("POST", "https://id.preprod.eta.gov.eg/connect/token",
            new Dictionary<string, string> { ["posserial"] = "MTJ-1", ["presharedkey"] = "k" }, "grant_type=client_credentials&client_id=c", "application/x-www-form-urlencoded"));

        Assert.Equal((200, "{\"access_token\":\"t\"}", true), (answer.StatusCode, answer.Body, answer.IsSuccess));
        Assert.Equal((HttpMethod.Post, "https://id.preprod.eta.gov.eg/connect/token"), (handler.Last!.Method, handler.Last.RequestUri!.ToString()));
        Assert.Equal(("MTJ-1", "k"), (handler.Last.Headers.GetValues("posserial").Single(), handler.Last.Headers.GetValues("presharedkey").Single()));
        Assert.Equal("application/x-www-form-urlencoded", handler.Last.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("grant_type=client_credentials&client_id=c", handler.LastBody);
    }

    [Fact]
    public async Task A_bearer_token_and_a_JSON_body_and_the_ETAs_wait_are_carried()
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"error\":{}}") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            return response;
        });

        var answer = await new HttpFiscalTransport(new HttpClient(handler)).SendAsync(new FiscalHttpRequest("POST",
            "https://api.preprod.invoicing.eta.gov.eg/api/v1/receiptsubmissions", new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }, "{\"receipts\":[]}"));

        Assert.Equal((422, TimeSpan.FromSeconds(600), false), (answer.StatusCode, answer.RetryAfter, answer.IsSuccess));
        Assert.Equal(("Bearer", "abc"), (handler.Last!.Headers.Authorization!.Scheme, handler.Last.Headers.Authorization.Parameter));
        Assert.Equal("application/json", handler.Last.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task No_connection_or_a_plain_HTTP_address_is_an_answer_with_status_0()
    {
        var offline = new HttpFiscalTransport(new HttpClient(new Handler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable",
            new SocketException((int)SocketError.NetworkUnreachable)))));
        var noConnection = await offline.SendAsync(new FiscalHttpRequest("GET", "https://api.invoicing.eta.gov.eg/x", new Dictionary<string, string>()));
        Assert.Equal((0, false, "no connection to the internet or to the tax authority"), (noConnection.StatusCode, noConnection.Answered, noConnection.Body));

        var plain = await offline.SendAsync(new FiscalHttpRequest("GET", "http://api.invoicing.eta.gov.eg/x", new Dictionary<string, string>()));
        Assert.Equal((0, "The tax authority's address must be HTTPS."), (plain.StatusCode, plain.Body));
    }
}
