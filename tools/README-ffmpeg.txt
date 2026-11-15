@echo off
REM Copies ffmpeg/ffprobe next to the app output when present on PATH.
where ffmpeg >nul 2>&1 || (echo ffmpeg not found on PATH & exit /b 1)
echo Use system ffmpeg from PATH, or place ffmpeg.exe and ffprobe.exe in tools\ffmpeg\
