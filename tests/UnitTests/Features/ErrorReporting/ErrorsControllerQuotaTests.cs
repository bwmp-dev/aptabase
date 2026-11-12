using Xunit;
using Moq;
using AwesomeAssertions;
using Aptabase.Data;
using Aptabase.Features;
using Aptabase.Features.Apps;
using Aptabase.Features.ErrorReporting;
using Aptabase.Features.ErrorReporting.Buffer;
using Aptabase.Features.Ingestion;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Aptabase.UnitTests.Features.ErrorReporting;

public class ErrorsControllerQuotaTests
{
    private const string AppId = "APP-123";

    private readonly Mock<IIssueStore> _issues = new();
    private readonly Mock<IErrorBuffer> _buffer = new();
    private readonly Mock<IPiiSanitizer> _piiSanitizer = new();
    private readonly Mock<IErrorQueryClient> _errorQueryClient = new();
    private readonly Mock<IDbContext> _db = new();

    public ErrorsControllerQuotaTests()
    {
        _piiSanitizer
            .Setup(s => s.Sanitize(It.IsAny<string?>()))
            .Returns<string?>(text => text ?? "");
    }

    [Fact]
    public async Task Should_Skip_Quota_Check_When_Quota_Is_Disabled()
    {
        var controller = CreateController(errorQuotaEnabled: false);

        var result = await controller.PostError(CreateValidBody(), default);

        result.Should().BeOfType<AcceptedResult>();
        _buffer.Verify(b => b.Add(ref It.Ref<TrackingError>.IsAny), Times.Once());

        // Quota disabled must mean zero Postgres round-trips
        _db.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(IssueRecordResult.QuotaExceeded, 403)]
    [InlineData(IssueRecordResult.AppUnavailable, 400)]
    public async Task Rejected_Reports_Are_Not_Buffered(IssueRecordResult outcome, int status)
    {
        _issues.Setup(s => s.Record(It.IsAny<TrackingError>(), default, true)).ReturnsAsync(outcome);
        var result = await CreateController(true).PostError(CreateValidBody(), default);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        _buffer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Duplicate_Is_Accepted_Without_Buffering_And_Uses_Canonical_Event_Id()
    {
        var id = Guid.NewGuid();
        var body = CreateValidBody();
        body.EventId = id.ToString("N").ToUpperInvariant();
        _issues.Setup(s => s.Record(It.IsAny<TrackingError>(), default, true)).ReturnsAsync(IssueRecordResult.Duplicate);
        Assert.IsType<AcceptedResult>(await CreateController(true).PostError(body, default));
        _issues.Verify(s => s.Record(It.Is<TrackingError>(e => e.ErrorId == id.ToString()), default, true), Times.Once());
        _buffer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Records_Sanitized_Issue_Before_Buffering()
    {
        _piiSanitizer.Setup(s => s.Sanitize(It.IsAny<string?>())).Returns("redacted");
        var controller = CreateController(errorQuotaEnabled: false);
        await controller.PostError(CreateValidBody(), default);
        _issues.Verify(s => s.Record(It.Is<TrackingError>(e => e.AppId == AppId &&
            e.ErrorType == "redacted" && e.ErrorMessage == "redacted" && e.StackTrace == "redacted"), default, false), Times.Once());
        _buffer.Verify(b => b.Add(ref It.Ref<TrackingError>.IsAny), Times.Once());
    }

    [Fact]
    public async Task Does_Not_Accept_Or_Buffer_When_Issue_Persistence_Fails()
    {
        _issues.Setup(s => s.Record(It.IsAny<TrackingError>(), default, false)).ThrowsAsync(new InvalidOperationException("database unavailable"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController(false).PostError(CreateValidBody(), default));
        _buffer.Verify(b => b.Add(ref It.Ref<TrackingError>.IsAny), Times.Never());
    }

    [Fact]
    public async Task Invalid_Report_Does_Not_Create_Issue()
    {
        var body = CreateValidBody();
        body.ErrorMessage = "";
        Assert.IsType<BadRequestObjectResult>(await CreateController(false).PostError(body, default));
        _issues.VerifyNoOtherCalls();
        _buffer.VerifyNoOtherCalls();
    }

    private ErrorsController CreateController(bool errorQuotaEnabled)
    {
        var controller = new ErrorsController(
            _buffer.Object,
            _issues.Object,
            _piiSanitizer.Object,
            _errorQueryClient.Object,
            _db.Object,
            CreateEnvSettings(errorQuotaEnabled),
            Mock.Of<ILogger<ErrorsController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        // In the pipeline ResolveErrorAppKey resolves the App-Key before the action runs
        ResolveAppKeyAttribute.SetApp(controller.HttpContext, new CachedApplication(new Application { Id = AppId }));
        return controller;
    }

    // EnvSettings can only be populated from environment variables (EnvSettings.Load), so
    // tests create an instance via its private constructor and flip the single flag needed.
    private static EnvSettings CreateEnvSettings(bool errorQuotaEnabled)
    {
        var env = (EnvSettings)Activator.CreateInstance(typeof(EnvSettings), nonPublic: true)!;
        typeof(EnvSettings)
            .GetProperty(nameof(EnvSettings.ErrorQuotaEnabled))!
            .SetValue(env, errorQuotaEnabled);
        return env;
    }

    private static ErrorBody CreateValidBody() => new()
    {
        ErrorMessage = "Object reference not set to an instance of an object",
        ErrorType = "NullReferenceException",
    };
}
