global using Xunit;

// Surfaces the down-level test-only extension methods (WaitAsync timeout/token,
// Stream.WriteAsync(byte[])) to every test file so the suite compiles on net48 without
// per-file imports. On net6.0/net8.0 the built-in instance methods take precedence.
global using SoftAgility.Beacon.Tests.Helpers;
