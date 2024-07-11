using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System;
using System.Linq;
using System.Collections.Generic;

var builder = WebApplication.CreateBuilder(args);

// Configure HttpClient to ignore SSL certificate errors
builder.Services.AddHttpClient("insecure").ConfigurePrimaryHttpMessageHandler(() =>
{
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
    };
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseRouting();

app.UseEndpoints(endpoints =>
{
    endpoints.Map("{**path}", async context =>
    {
        var clientFactory = context.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = clientFactory.CreateClient("insecure");

        var originalPath = context.Request.Path.ToString().TrimStart('/');
        var pathSegments = originalPath.Split('/');

        if (pathSegments.Length < 2)
        {
            context.Response.StatusCode = 400; // Bad Request
            await context.Response.WriteAsync("Invalid path format. Expected format: /scheme/domain:port/url_fragments or /domain:port/url_fragments");
            return;
        }

        string scheme = "";
        string domainWithPort = "";
        string urlFragments = "";

        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        try
        {
            if (pathSegments[0] == "http" || pathSegments[0] == "https")
            {
                // Full URL is provided in the path
                scheme = pathSegments[0];
                domainWithPort = pathSegments[1];
                urlFragments = string.Join('/', pathSegments.Skip(2));
            }
            else
            {
                // Scheme is not provided, use Referer header to determine the original URL
                if (!context.Request.Headers.TryGetValue("Referer", out var refererValues))
                {
                    context.Response.StatusCode = 400; // Bad Request
                    await context.Response.WriteAsync("Referer header is required for in-page assets without scheme.");
                    return;
                }

                var referer = refererValues.First();
                var refererUri = new Uri(referer);

                var refererPathSegments = refererUri.PathAndQuery.TrimStart('/').Split('/');

                if (refererPathSegments.Length < 2)
                {
                    context.Response.StatusCode = 400; // Bad Request
                    await context.Response.WriteAsync("Invalid referer URL format.");
                    return;
                }

                scheme = refererUri.Scheme;
                domainWithPort = refererPathSegments[0];
                urlFragments = string.Join('/', pathSegments);
            }

            var targetUri = new Uri($"{scheme}://{domainWithPort}/{urlFragments}");

            var requestMessage = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = targetUri
            };

            // Copy request headers
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.Host = targetUri.Host;
                }
                else if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value))
                {
                    requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
                }
            }

            // Modify the Origin header if it exists
            if (context.Request.Headers.TryGetValue("Origin", out var originValues))
            {
                var origin = originValues.First();
                var uri = new Uri(origin);
                var manipulatedOrigin = $"{uri.Scheme}://{domainWithPort}";
                requestMessage.Headers.Remove("Origin");
                requestMessage.Headers.Add("Origin", manipulatedOrigin);
            }

            // Logging request details
            logger.LogInformation($"Forwarding request to {targetUri}");

            HttpResponseMessage responseMessage;

            try
            {
                responseMessage = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error forwarding request");
                context.Response.StatusCode = 502; // Bad Gateway
                await context.Response.WriteAsync("Error forwarding request");
                return;
            }

            context.Response.StatusCode = (int)responseMessage.StatusCode;

            foreach (var header in responseMessage.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in responseMessage.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            // Remove the X-Frame-Options header
            context.Response.Headers.Remove("X-Frame-Options");

            context.Response.Headers.Remove("transfer-encoding");

            await responseMessage.Content.CopyToAsync(context.Response.Body);
        }
        catch (UriFormatException ex)
        {
            logger.LogError(ex, $"Invalid URI format: {scheme}://{domainWithPort}/{urlFragments}");
            context.Response.StatusCode = 400; // Bad Request
            await context.Response.WriteAsync("Invalid URI format.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An unexpected error occurred");
            context.Response.StatusCode = 500; // Internal Server Error
            await context.Response.WriteAsync("An unexpected error occurred.");
        }
    });
});

app.Run();
