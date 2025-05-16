#!/usr/bin/env node

/**
 * Copyright (c) 2024 John Jung
 * Simple test script to run the Vitally MCP server directly
 */
import { spawn } from 'node:child_process';
import * as path from 'node:path';
import * as fs from 'node:fs';
import { fileURLToPath } from 'node:url';

// Get current directory for ES modules
const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Get the absolute path to the server
const serverPath = path.resolve(__dirname, 'build/index.js');

if (!fs.existsSync(serverPath)) {
  console.error(`Error: Server script not found at ${serverPath}`);
  console.error('Make sure you have built the project with "npm run build"');
  process.exit(1);
}

console.log(`Starting Vitally MCP server from: ${serverPath}`);

// Run the server
const server = spawn('node', [serverPath], {
  stdio: 'inherit', // This will show stdout/stderr directly in the console
});

console.log('Server started! Press Ctrl+C to stop.');

// Handle server exit
server.on('close', (code) => {
  console.log(`Server process exited with code ${code}`);
});

// Handle errors
server.on('error', (err) => {
  console.error('Failed to start server:', err);
}); 