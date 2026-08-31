@echo off
REM ============================================================
REM  构建内置向量数据库 Rust DLL (BuiltInVectorDb.dll)
REM  产物: NATIVE\vector-db-rust\target\release\BuiltInVectorDb.dll
REM ============================================================
setlocal
set SCRIPT_DIR=%~dp0
cd /d "%SCRIPT_DIR%"

echo [1/2] 构建 release DLL ...
call cargo build --release
if errorlevel 1 (
    echo 构建失败！
    exit /b 1
)

echo [2/2] 运行单元测试 ...
call cargo test
if errorlevel 1 (
    echo 测试失败！
    exit /b 1
)

echo.
echo 构建完成：%SCRIPT_DIR%target\release\BuiltInVectorDb.dll
echo 将其复制到 C# 程序输出目录（如 bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\）
echo 或程序根目录，主程序启动时会自动加载。
endlocal