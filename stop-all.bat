@echo off
REM Stops every RateLimit service. Run this before building from the IDE: a running
REM service locks its own binary and the build fails with MSB3021.
REM Keep this file ASCII-only - non-ASCII bytes break cmd's line parsing.
setlocal
for %%S in (InstagramGraphMock InstagramSenderApi WebhookIngestApi WebhookTrafficSimulator) do (
    taskkill /F /IM "%%S.exe" >nul 2>&1 && echo stopped %%S || echo %%S was not running
)
endlocal
REM taskkill exits 128 when a process was not running; nothing failed, so report success.
exit /b 0
