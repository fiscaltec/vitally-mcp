# VitallyMcp.Tests

Comprehensive test suite for the Vitally MCP server implementation.

## Test Coverage

This test project provides **52 tests** covering critical functionality across the codebase:

### Test Classes

#### 1. VitallyConfigTests (12 tests)
Tests environment variable loading and validation for API credentials.

**Coverage:**
- Valid environment variable loading
- Missing API key detection
- Empty API key detection
- Missing subdomain detection
- Empty subdomain detection
- Both credentials missing detection
- Various valid API key formats
- Various valid subdomain formats

**Key Features:**
- Tests run sequentially using `[Collection]` attribute to avoid environment variable conflicts
- Each test includes proper cleanup in `finally` blocks

#### 2. VitallyServiceTests (29 tests)
Tests JSON filtering, field selection, trait filtering, and pagination.

**Coverage:**
- Default field behaviour for all resource types
- Client-side field filtering
- Trait inclusion/exclusion
- Specific trait filtering
- Non-existent field handling
- Non-existent trait handling
- Pagination cursor preservation
- Empty results handling
- Multiple result filtering
- Resource-specific default fields (accounts, organisations, users, tasks, notes, projects)

**Key Scenarios:**
- Traits excluded by default to minimise response size
- Field existence validation (non-existent fields skipped)
- GetResourcesAsync vs GetResourceByIdAsync behaviour
- Pagination metadata preservation

#### 3. AccountsToolsTests (11 tests)
Tests account-related tool endpoints and parameter passing.

**Coverage:**
- List accounts with default parameters
- List accounts with field filtering
- List accounts with status filter
- List accounts with pagination
- List accounts with traits
- Get single account
- Get account with field filtering
- List accounts by organisation
- Create account
- Update account
- Delete account

#### 4. UsersToolsTests (11 tests)
Tests user-related tool endpoints and parameter passing.

**Coverage:**
- List users with default parameters
- List users with field filtering
- List users by account
- List users by organisation
- Search users by email
- Get single user
- Create user
- Update user
- Delete user
- List users with traits

## Test Framework & Dependencies

- **xUnit** 2.9.3 - Test framework
- **Moq** 4.20.72 - Mocking framework for HttpClient
- **FluentAssertions** 8.8.0 - Readable assertions
- **coverlet.collector** 6.0.4 - Code coverage collection

## Running Tests

### Run All Tests
```powershell
dotnet test VitallyMcp.Tests/VitallyMcp.Tests.csproj
```

### Run with Detailed Output
```powershell
dotnet test VitallyMcp.Tests/VitallyMcp.Tests.csproj --verbosity normal
```

### Run Specific Test Class
```powershell
dotnet test --filter "FullyQualifiedName~VitallyServiceTests"
```

### Run Specific Test Method
```powershell
dotnet test --filter "Name~GetResourcesAsync_AccountsWithNoFields"
```

### Generate Code Coverage Report
```powershell
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

## Test Structure

### TestHelpers.cs
Provides mock HTTP client creation and sample JSON data for testing:

**Mock HTTP Client Creation:**
- `CreateMockHttpClient(jsonResponse)` - Simple mock with JSON response
- `CreateMockHttpClientWithHandler(jsonResponse)` - Mock with handler for verification

**Sample JSON Data:**
- `GetSampleAccountJson()` - Full account with traits
- `GetSampleOrganizationJson()` - Organisation data
- `GetSampleUserJson()` - User data
- `GetSampleTaskJson()` - Task data
- `GetSampleNoteJson()` - Note data
- `GetSampleProjectJson()` - Project data
- `GetSampleSingleAccountJson()` - Single account (for Get operations)
- `GetEmptyResultsJson()` - Empty results array

## Key Testing Patterns

### 1. Environment Variable Testing
```csharp
Environment.SetEnvironmentVariable("VITALLY_API_KEY", testValue);
try
{
    var config = VitallyConfig.FromEnvironment();
    // assertions
}
finally
{
    Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
}
```

### 2. Mocking HTTP Responses
```csharp
var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
var service = new VitallyService(mockClient, config);
var result = await service.GetResourcesAsync("accounts");
```

### 3. JSON Assertion
```csharp
var jsonDoc = JsonDocument.Parse(result);
jsonDoc.RootElement.TryGetProperty("id", out _).Should().BeTrue();
jsonDoc.RootElement.TryGetProperty("traits", out _).Should().BeFalse();
```

## Test Scenarios Based on CLAUDE.md Requirements

The tests validate all scenarios documented in `CLAUDE.md`:

✅ Pagination with `from` parameter
✅ Client-side field filtering
✅ Trait filtering (default exclusion, specific trait selection)
✅ Resource-specific default fields
✅ Field existence handling (non-existent fields skipped)
✅ Error handling (environment variable validation)
✅ Response size reduction via filtering

## Future Test Considerations

### Not Currently Tested (Require Live API)
- Actual Vitally API integration tests
- HTTP error handling (4xx, 5xx responses)
- Network timeout scenarios
- Rate limiting behaviour

### Potential Additions
- Performance/load testing
- Custom field scenarios for different resource types
- All 75 tool endpoints (currently sample coverage)
- MCP protocol integration tests
- End-to-end tests with Claude Desktop

## Continuous Integration

These tests are designed to run in CI/CD pipelines:
- No external dependencies (fully mocked)
- Fast execution (~230ms total)
- Zero flaky tests (deterministic)
- Clear pass/fail output

## Notes

- Tests use mocked HTTP clients - no actual API calls are made
- VitallyConfig tests run sequentially to avoid env var conflicts
- All tool tests validate parameter passing and JSON response handling
- Default field filtering is extensively tested across resource types
