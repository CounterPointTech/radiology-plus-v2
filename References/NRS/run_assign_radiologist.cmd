@echo off
setlocal

set PGPASSWORD=4fPhMULkrdwv!
set LOGFILE=D:\NRS\logs\assign_radiologist_%date:~-4,4%%date:~-10,2%%date:~-7,2%.log

echo ===== Run at %date% %time% ===== >> "%LOGFILE%"

echo Checking if psql exists... >> "%LOGFILE%"
if exist "c:\program files\postgresql\14\bin\psql.exe" (
	echo psql.exe found! >> "%LOGFILE%"
) else (
	echo ERROR: psql.exe not found at expected path! >> "%LOGFILE%"
	goto :end
)

echo Verifying SQL file exists... >> "%LOGFILE%"
if exist "D:\NRS\assign_radiologist.sql" (
	echo SQL File found! >> "%LOGFILE%"
) else (
	echo ERROR: SQL File not found! >> "%LOGFILE%"
	goto :end
)

echo Running query... >> "%LOGFILE%"
"c:\program files\postgresql\14\bin\psql.exe" --host=127.0.0.1 --dbname=novarad --port=5432 --username=mirth2 -w --file=D:\NRS\assign_radiologist.sql >> "%LOGFILE%" 2>&1

if %ERRORLEVEL% neq 0 (
	echo ERROR: Query failed with error code %ERRORLEVEL% >> "%LOGFILE%"
) else (
	echo SUCCESS >> "%LOGFILE%"
)
endlocal

