# Vitally MCP Server

A Model Context Protocol (MCP) server that provides seamless integration with the Vitally customer success platform. This server enables you to query and interact with your Vitally data through standardised MCP tools, resources, and prompts.

## Features

### Tools

- **Account Management**: List, search, and retrieve account information with full pagination support
- **User Management**: Access user data and associated records  
- **Task Management**: List, create, and manage tasks with filtering and pagination
- **Notes**: Access and create customer notes with pagination
- **Conversations**: Retrieve customer conversation history with pagination
- **Organizations & Projects**: Access organizational data and project information with pagination
- **NPS Responses**: Query Net Promoter Score feedback with pagination
- **Pagination Helper**: Advanced tool to automatically fetch multiple pages of data

### Pagination Features

- **Cursor-based pagination**: Efficient pagination using Vitally's cursor system
- **Rate limit awareness**: Automatic rate limit monitoring and reporting
- **Configurable limits**: Support for 1-100 items per page (API maximum)
- **Sort options**: Sort by `updatedAt` (default) or `createdAt`
- **Bulk fetching**: `paginate-all` tool for fetching multiple pages automatically
- **Pagination metadata**: Detailed pagination information in responses

### Resources

- **Account Summary**: Comprehensive account overview with recent activity
- **Organization Summary**: Organization details with associated accounts and activity
- **Health Check**: Server status and API connectivity verification

### Prompts

- **Account Health Check**: Automated account health analysis
- **Weekly Account Report**: Structured weekly account reporting

## Prerequisites

- Node.js 18 or higher
- A Vitally account with API access
- Vitally API credentials (subdomain and API key)

## Installation

1. **Clone or create the project structure**:

```bash
mkdir vitally-mcp-server
cd vitally-mcp-server
```

2. **Install dependencies**:

```bash
npm install
```

3. **Build the project**:

```bash
npm run build
```

## Configuration

### 1. Obtain Vitally API Credentials

1. Log into your Vitally account
2. Navigate to Settings → Integrations → Vitally REST API
   - Alternatively use Quick Jump: `⌘ + j` (Mac) or `Alt + j` (Windows)
3. Toggle the integration switch to enable
4. Copy your API key and note your subdomain

### 2. Set Environment Variables

Create a `.env` file in your project root:

```bash
VITALLY_SUBDOMAIN=your-company-subdomain
VITALLY_API_KEY=your-api-key-here
```

**Important**: Your subdomain is found in your Vitally URL (e.g., `https://yoursubdomain.vitally.io`)

## Usage

### Development Mode

```bash
npm run dev
```

### Production Mode

```bash
npm start
```

### With MCP Client

Configure your MCP client to connect to this server via stdio transport.

## API Reference

### Account Tools

#### `list-accounts`

List accounts with comprehensive pagination support.

```typescript
{
  limit?: number (1-100, default: 50),    // Max items per page
  from?: string,                          // Cursor from previous request
  sortBy?: "updatedAt" | "createdAt"      // Sort order (default: updatedAt)
}
```

**Response includes:**

- `results`: Array of account objects
- `next`: Cursor for next page (null if no more pages)
- `_pagination`: Metadata including hasMore, nextCursor, currentLimit, sortBy
- `_rateLimitInfo`: Current rate limit status

#### `search-accounts`

Search accounts by name or external ID with pagination.

```typescript
{
  name?: string,                          // Search by name (partial match)
  externalId?: string,                    // Get by external ID
  limit?: number (1-100, default: 50)    // Max items to return
}
```

### Pagination Tools

#### `paginate-all`

Automatically fetch multiple pages of data from any endpoint.

```typescript
{
  endpoint: "accounts" | "users" | "tasks" | "notes" | "conversations" | "organizations" | "projects" | "npsResponses",
  accountId?: string,                     // Filter by account (supported endpoints)
  organizationId?: string,                // Filter by organization (supported endpoints)  
  userId?: string,                        // Filter by user (NPS responses only)
  maxPages?: number (1-10, default: 5),   // Safety limit on pages to fetch
  pageSize?: number (1-100, default: 100), // Items per page
  sortBy?: "updatedAt" | "createdAt"      // Sort order
}
```

**Example Usage:**

```typescript
// Fetch all tasks for an account (up to 5 pages of 100 items each)
{
  "endpoint": "tasks",
  "accountId": "account-123",
  "maxPages": 5,
  "pageSize": 100
}
```

**Response includes:**

- `results`: Combined array of all fetched items
- `summary`: Detailed information about the pagination process
  - `totalFetched`: Total number of items retrieved
  - `pagesFetched`: Number of pages processed
  - `hasMorePages`: Whether more data is available
  - `nextCursor`: Cursor to continue from (if stopped at maxPages limit)

### Pagination Best Practices

1. **Use appropriate page sizes**: Start with smaller limits (50) for exploration, use larger limits (100) for bulk operations
2. **Monitor rate limits**: Check `_rateLimitInfo` in responses to avoid hitting API limits
3. **Choose the right sort order**:
   - Use `updatedAt` (default) for recent activity
   - Use `createdAt` when you need consistent ordering during data sync
4. **Use `paginate-all` for bulk operations**: When you need all data from an endpoint
5. **Save cursors**: Store the `next` cursor to resume pagination later

#### `create-task`

Create a new task.

```typescript
{
  name: string,
  accountId?: string,
  organizationId?: string,
  externalId?: string,
  description?: string,
  assignedToId?: string,
  dueDate?: string,
  categoryId?: string,
  traits?: Record<string, any>
}
```

### Note Tools

#### `list-notes`

List notes with optional filtering.

```typescript
{
  accountId?: string,
  organizationId?: string,
  limit?: number (default: 50),
  from?: string
}
```

#### `create-note`

Create a new note.

```typescript
{
  subject: string,
  note: string,
  accountId?: string,
  organizationId?: string,
  externalId?: string,
  authorId?: string,
  noteDate?: string,
  categoryId?: string,
  traits?: Record<string, any>,
  tags?: string[]
}
```

### Resources

#### `vitally://accounts/{accountId}/summary`

Provides a comprehensive account summary including:

- Account details
- Recent tasks (last 10)
- Recent notes (last 5)
- Recent conversations (last 5)

#### `vitally://organizations/{organizationId}/summary`

Provides organization overview including:

- Organization details
- Associated accounts (up to 20)
- Recent tasks (last 10)
- Recent notes (last 5)

#### `vitally://health`

Server health check and API connectivity status.

### Prompts

#### `account-health-check`

Analyzes account health including task completion, communication frequency, and engagement patterns.

```typescript
{
  accountId: string
}
```

#### `weekly-account-report`

Generates comprehensive weekly account reports.

```typescript
{
  accountId: string,
  startDate?: string
}
```

## Error Handling

The server includes comprehensive error handling for:

- Authentication failures
- Network connectivity issues
- Invalid parameters
- API rate limiting
- Malformed requests

All errors are returned with descriptive messages to help with troubleshooting.

## Rate Limiting

Vitally's API has a default rate limit of 1,000 requests per minute using a sliding window. The server respects these limits and includes rate limiting information in response headers.

## Development

### Available Scripts

- `npm run build` - Build TypeScript to JavaScript
- `npm run dev` - Run in development mode with auto-reload
- `npm run lint` - Run ESLint code analysis
- `npm test` - Run test suite

### Project Structure

```
src/
  index.ts          # Main server implementation
dist/               # Compiled JavaScript (generated)
package.json        # Dependencies and scripts
tsconfig.json       # TypeScript configuration
.eslintrc.js        # ESLint configuration
jest.config.js      # Test configuration
```

## Troubleshooting

### Common Issues

1. **Authentication Errors**
   - Verify your API key is correct
   - Ensure your subdomain matches your Vitally URL
   - Check that the API integration is enabled in Vitally

2. **Network Errors**
   - Verify internet connectivity
   - Check if your firewall blocks outbound HTTPS requests
   - Ensure your Vitally subdomain is accessible

3. **Data Not Found**
   - Verify the IDs you're using exist in Vitally
   - Check if you have the necessary permissions for the data
   - Ensure you're using the correct ID type (internal vs external)

### Debug Mode

Set `NODE_ENV=development` for detailed logging and error information.

## Security Considerations

- Store API credentials securely using environment variables
- Never commit API keys to version control
- Regularly rotate your API keys in Vitally
- Monitor API usage and set up alerts for unusual activity
- Use HTTPS for all communications

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes with tests
4. Run the test suite
5. Submit a pull request

## License

MIT License - see LICENSE file for details.

## Support

For issues related to:

- **MCP Server**: Create an issue in this repository
- **Vitally API**: Consult the [Vitally API documentation](https://docs.vitally.io/)
- **MCP Protocol**: See the [Model Context Protocol documentation](https://modelcontextprotocol.io)

---

**Note**: This server is designed for the Vitally REST API and requires valid API credentials. Ensure you have the necessary permissions and comply with your organization's data access policies.
