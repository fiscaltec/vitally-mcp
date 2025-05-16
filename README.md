<!-- Copyright (c) 2024 John Jung -->

# Vitally MCP Server

An MCP (Model Context Protocol) server that provides access to Vitally customer data via the Vitally API.

## Features

- List customer accounts as resources
- Read account details
- Search for users by email or external ID
- Find accounts by name
- Query account health scores
- View account conversations and tasks
- Create notes for accounts
- Search through available tools
- Demo mode with mock data when no API key is provided

## Setup

1. Install dependencies:
   ```
   npm install
   ```

2. Create a `.env` file in the root directory with the following:
   ```
   # Vitally API Configuration
   VITALLY_API_SUBDOMAIN=nylas  # Your Vitally subdomain
   VITALLY_API_KEY=your_api_key_here  # Your Vitally API key
   VITALLY_DATA_CENTER=US  # or EU depending on your data center
   ```

3. Build the project:
   ```
   npm run build
   ```

> **Note:** If you don't have a Vitally API key yet, the server will run in demo mode with mock data.

## Getting your Vitally API Key

1. Navigate to your Vitally account
2. Go to Settings (⚙️) > Integrations > REST API
3. Toggle the switch to enable the integration
4. Copy the API Key

## Usage

There are two ways to use this MCP server:

### Using the MCP Inspector

Run the MCP Inspector to test and debug the server:

```
npm run inspector
```

This will open the MCP Inspector interface where you can interact with your server.

### Connect to Claude Desktop

1. First, find your Claude Desktop configuration file:
   - On macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
   - On Windows: `%APPDATA%\Claude\claude_desktop_config.json`

2. Edit the config file to add the Vitally MCP server:
   ```json
   {
     "mcpServers": {
       "vitally-api": {
         "command": "node",
         "args": ["--experimental-modules", "--experimental-specifier-resolution=node", "/Users/johnjung/nylas/vitally/vitally/build/index.js"]
       }
     }
   }
   ```

3. Restart Claude Desktop and you'll be able to use the Vitally MCP server.

## Available Tools

### Tool Discovery
- `search_tools` - Search for available tools by keyword

### Account Management
- `search_accounts` - Search for accounts using multiple criteria (name, externalId)
- `find_account_by_name` - Find accounts by their name (partial matching supported)
- `refresh_accounts` - Refresh the cached list of accounts
- `get_account_health` - Get health scores for a specific account

### User Management
- `search_users` - Search for users by email, external ID, or email subdomain

### Communication & Tasks
- `get_account_conversations` - Get recent conversations for an account
- `get_account_tasks` - Get tasks for an account (can filter by status)
- `create_account_note` - Create a new note for an account

## Example Questions to Ask

When connected to an MCP client like Claude, you can ask questions such as:

- "List all our customers"
- "Find accounts with 'Acme' in their name"
- "What's the health score for account X?"
- "Find user with email example@company.com"
- "Show me details about customer Y"
- "Get recent conversations for account Z"
- "What tasks are open for account A?"
- "Add a note to account B about our recent call"
- "What tools can I use for account management?"

## Troubleshooting

- If you encounter JSON parsing errors, ensure you've removed all console.log statements from the code
- Make sure your `.env` file contains the correct API credentials
- Check that you've built the project (`npm run build`) after making changes
- Verify the path in claude_desktop_config.json is absolute and correct for your system
- If you don't have a valid API key, the server will run in demo mode with mock data
