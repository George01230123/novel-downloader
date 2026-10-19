@echo off
rem ============================================================
rem  Build script for the novel downloader (no Visual Studio needed).
rem
rem  IMPORTANT: this file is kept pure ASCII on purpose.
rem  cmd.exe reads .bat files with the OEM codepage (GBK on zh-CN),
rem  so UTF-8 Chinese text inside a .bat gets mangled and can even
rem  split a command line in half (it eats the next line's %~dp0).
rem  All Chinese output is printed by the compiled programs instead.
rem
rem  Source layout:
rem    src\            UI entry (Program.cs, MainForm.cs, AssemblyInfo.cs)
rem    src\Common\      shared: models, http, cache, download runner
rem    src\Sites\       site adapters: biquga PC / biquga mobile / fanqie
rem    tests\           EdgeTest, TestMain (command line self tests)
rem    tools\           QuickDownload, DiagMobile (dev helpers)
rem ============================================================
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" goto nocsc

if not exist "dist" mkdir "dist"

echo [1/6] Normalizing source encodings (.bat ASCII only, others UTF-8 with BOM)...
powershell -NoProfile -ExecutionPolicy Bypass -File "build\fix-encoding.ps1"
if errorlevel 1 goto failed

rem /codepage:65001 -> read sources as UTF-8 even without a BOM
set FLAGS=/nologo /platform:anycpu /optimize+ /codepage:65001 /nowarn:1591,0618
rem System.IO.Compression[.FileSystem]: needed by the EPUB export (hand-written zip,
rem zero third-party dependencies).
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
set COMMON=src\Common\Models.cs src\Common\Log.cs src\Common\Http.cs src\Common\AppSettings.cs src\Common\DirCache.cs src\Common\FontMap.cs src\Common\DownloadRunner.cs src\Common\EpubWriter.cs src\Common\ChapterIndex.cs src\Common\SiteProfile.cs src\Common\MarkdownWriter.cs src\Common\CoverFetcher.cs src\Common\BookStats.cs src\Common\Bookshelf.cs src\Common\UpdateChecker.cs src\Common\ZhConvert.cs src\Common\NetDiag.cs src\Common\TypoFinder.cs src\Common\CrawlResume.cs src\Common\AiAdjudicator.cs src\Common\SearchIndex.cs src\Common\HttpBatch.cs src\Common\SiteRegistry.cs src\Common\SiteRule.cs src\Common\SiteRules.cs src\Common\SiteRater.cs src\Common\ConsoleSetup.cs src\Common\UiFont.cs src\Common\SiteDiagnostics.cs src\Common\ChapterNumber.cs src\Common\LocalBackup.cs src\Common\DiagnosticsExporter.cs src\Common\SiteHealthDialog.cs src\Common\SitePicker.cs 
set SITES=src\Sites\BiqugaSite.cs src\Sites\BiqugaMobileSite.cs src\Sites\FanqieSite.cs src\Sites\RuleSite.cs src\Sites\TomatoCore.cs
echo [2/6] Building main program (GUI)...
rem OfflineTests.cs is linked in ONLY for this target (not for the other 6), and
rem /define:SELFTEST is set, so it lets the GUI exe run the full offline suite via
rem `--selftest` from the SAME binary the user is running -- a bug report becomes one
rem command instead of "please also download _offlinetests.exe".
rem /main is required because OfflineTests.cs also declares a Main (it is the entry
rem point of _offlinetests.exe).
"%CSC%" %FLAGS% /target:winexe /main:TomatoBiquga.Program /define:SELFTEST /out:"dist\_build_tmp.exe" %REFS% ^
 src\AssemblyInfo.cs src\Program.cs src\MainForm.cs tests\OfflineTests.cs %COMMON% %SITES%
if errorlevel 1 goto failed

echo [3/6] Building command line self tests...
"%CSC%" %FLAGS% /target:exe /out:"dist\_selftest.exe" %REFS% ^
 tests\TestMain.cs %COMMON% %SITES%
if errorlevel 1 goto failed

"%CSC%" %FLAGS% /target:exe /main:EdgeTest /out:"dist\_edgetest.exe" %REFS% ^
 tests\EdgeTest.cs src\Program.cs src\MainForm.cs %COMMON% %SITES%
if errorlevel 1 goto failed

rem Layout probe: opens the real form offscreen, reports every control that overflows its
rem parent or overlaps a sibling, and can save screenshots (--shots). Handy when touching
rem the UI. Previously it was compiled by hand -- which is exactly how the CI breakage of
rem v1.0.2 happened (one build path updated, the other forgotten), so it lives here now.
"%CSC%" %FLAGS% /target:exe /main:TomatoBiquga.LayoutProbe /out:"dist\_layoutprobe.exe" %REFS% ^
 src\AssemblyInfo.cs src\Program.cs src\MainForm.cs tools\LayoutProbe.cs %COMMON% %SITES%
if errorlevel 1 goto failed

rem Text-fit probe: measures every button label with TextRenderer and fails if the
rem button is too narrow for its own text. Needed because the layout probe only checks
rem overflow/overlap -- a button squeezed to "???..." is neither, so it passes geometry
rem while the user cannot read the UI at all. That exact bug shipped: MakeButton set a
rem hardcoded Width plus AutoEllipsis, so on a font/DPI where the label measured wider
rem than the design value, every button lost its text.
"%CSC%" %FLAGS% /target:exe /main:TextFitProbe /out:"dist\_textfit.exe" %REFS% ^
 src\AssemblyInfo.cs src\Program.cs src\MainForm.cs tools\TextFitProbe.cs %COMMON% %SITES%
if errorlevel 1 goto failed

rem AI probe: starts a FAKE OpenAI/Ollama server on a loopback port and drives the real
rem AiAdjudicator against it -- no model, no internet needed. Modes: openai / ollama / err.
rem Why it exists: the pure-function tests covered "build the JSON" and "parse the reply"
rem but NOT "does the verdict land back on the Diff". It did not -- verdicts[] got filled
rem while diffs[].Ai stayed null, so the report showed nothing at all. Only a real
rem end-to-end pass over HTTP caught that.
"%CSC%" %FLAGS% /target:exe /main:AiMockProbe /out:"dist\_aimock.exe" %REFS% ^
 src\AssemblyInfo.cs src\Program.cs src\MainForm.cs tools\AiMockProbe.cs %COMMON% %SITES%
if errorlevel 1 goto failed

echo [4/6] Building offline unit tests (no network needed, used by CI)...
rem DirCache.KeyFor calls a static BiqugaSite helper, so the site sources are linked in
rem as well. MainForm is linked too: the layout self-test builds a real (offscreen) form
rem and asserts that no control overflows its parent or overlaps another one.
rem The test performs no network access and writes nothing to dist\cache.
"%CSC%" %FLAGS% /target:exe /main:TomatoBiquga.OfflineTests /out:"dist\_offlinetests.exe" %REFS% ^
 src\AssemblyInfo.cs src\MainForm.cs tests\OfflineTests.cs %COMMON% %SITES%
if errorlevel 1 goto failed

rem End-to-end integration probe: builds a whole book (volumes + cover + traditional
rem Chinese) and then RE-OPENS the produced EPUB like a foreign file -- mimetype order,
rem every XML parsed by XmlDocument, cover bytes present, nav nesting exact.
rem Why it exists: unit tests were all green while the EPUB nav grouped one volume
rem per chapter. "Parts pass" does not mean "the produced book is valid".
"%CSC%" %FLAGS% /target:exe /main:E2E /out:"dist\_e2e.exe" %REFS% ^
 src\AssemblyInfo.cs src\Program.cs src\MainForm.cs tests\E2E.cs %COMMON% %SITES%
if errorlevel 1 goto failed

rem Live probe -- NEEDS NETWORK, so it is NOT part of the offline test gate:
rem   _liveprobe.exe cover  "https://www.biquga.com/10_10333/"
rem   _liveprobe.exe fanqie BOOK_ID
rem   _liveprobe.exe export DOWNLOAD_ROOT BOOK_TITLE [cache dir]
rem Why it must exist: a curl command line bug made EVERY request fail with
rem exit code 3 while all 691 offline assertions stayed green -- that class of
rem bug only shows up when a request is actually sent over the wire.
rem NOTE: keep rem lines free of parentheses. cmd.exe parses them even inside
rem rem, and a stray close-paren aborts the whole script with
rem "the was unexpected at this time".
"%CSC%" %FLAGS% /target:exe /main:LiveProbe /out:"dist\_liveprobe.exe" %REFS% ^
 src\AssemblyInfo.cs src\Program.cs src\MainForm.cs tools\LiveProbe.cs %COMMON% %SITES%
if errorlevel 1 goto failed

echo [5/6] Renaming GUI output (Chinese name, done in PowerShell)...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d=Join-Path (Get-Location) 'dist'; $src=Join-Path $d '_build_tmp.exe'; $dst=Join-Path $d ([char]0x5C0F+[char]0x8BF4+[char]0x4E0B+[char]0x8F7D+[char]0x5668+'.exe'); Move-Item -LiteralPath $src -Destination $dst -Force; Write-Host ('OK -> ' + $dst)"
if errorlevel 1 goto failed

echo [6/6] Done.


echo.
echo Build succeeded. Output folder: dist
dir /b "dist\*.exe"
echo.
pause
exit /b 0

:nocsc
echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
pause
exit /b 1

:failed
echo [ERROR] Build failed. See the messages above.
pause
exit /b 1
