# UsenetSharp

A high-performance .NET library for Usenet NNTP protocol with yEnc support.

## Features

- Asynchronous NNTP client implementation
- SSL/TLS support for secure connections
- Authentication support
- Article retrieval (ARTICLE, BODY, STAT commands)
- yEnc decoding via RapidYencSharp integration
- Thread-safe operations
- High-performance streaming

## Installation

Install via NuGet:

```bash
dotnet add package UsenetSharp
```

## Usage

### Basic Connection and Authentication

```csharp
using UsenetSharp.Clients;

var client = new UsenetClient();

// Connect to Usenet server
await client.ConnectAsync("news.example.com", 563, useSsl: true, cancellationToken);

// Authenticate
await client.AuthenticateAsync("username", "password", cancellationToken);
```

### Retrieving Articles

```csharp
// Get article body
var bodyResponse = await client.BodyAsync("article-id@example.com", cancellationToken);
using (var stream = bodyResponse.Stream)
{
    // Process article body stream
}

// Get full article (headers + body)
var articleResponse = await client.ArticleAsync("article-id@example.com", cancellationToken);
Console.WriteLine($"Headers: {articleResponse.Headers}");
using (var stream = articleResponse.Stream)
{
    // Process article stream
}
```

### Checking Article Existence with STAT

```csharp
// Check if an article exists (typically used for validation or inventory purposes)
// Note: In most cases, you would directly call BodyAsync or ArticleAsync
// rather than checking with STAT first
var statResponse = await client.StatAsync("article-id@example.com", cancellationToken);

if (statResponse.Exists)
{
    Console.WriteLine($"Article exists with response code: {statResponse.ResponseCode}");
}
else
{
    Console.WriteLine($"Article not found. Response code: {statResponse.ResponseCode}");
}
```

### Decoding yEnc Content with YencStream

```csharp
using UsenetSharp.Streams;

// Get article body that contains yEnc-encoded data
using var bodyResponse = await client.BodyAsync("yenc-article-id@example.com", cancellationToken);

// Wrap the body stream with YencStream for automatic decoding
using var yencStream = new YencStream(bodyResponse.Stream);

// Get yEnc headers (filename, size, part info, etc.)
var yencHeaders = await yencStream.GetYencHeadersAsync(cancellationToken);
if (yencHeaders != null)
{
    Console.WriteLine($"File: {yencHeaders.FileName}");
    Console.WriteLine($"Size: {yencHeaders.FileSize} bytes");
    Console.WriteLine($"Part: {yencHeaders.PartNumber}/{yencHeaders.TotalParts}");
}

// Process the decoded stream
// The yencStream can be read from directly and will provide decoded binary data
```

### Server Date/Time

```csharp
var dateResponse = await client.DateAsync(cancellationToken);
Console.WriteLine($"Server time: {dateResponse.DateTime}");
```

## Requirements

- .NET 9.0 or later
- RapidYencSharp package (automatically installed as dependency)

## Running Tests

Before running the unit tests, you need to update the credentials in `UsenetSharpTest/Credentials.cs` with your Usenet server details:

```csharp
public static class Credentials
{
    public const string Host = "news.example.com";
    public const string Username = "your-username";
    public const string Password = "your-password";
}
```

Once configured, run the tests using:

```bash
dotnet test
```

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Repository

https://github.com/nzbdav-dev/UsenetSharp
