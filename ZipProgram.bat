@echo off
echo currentPath:%~dp0

set zipName=NotionBackupHelpTool.zip
set programFolder=%currentPath%bin\Debug\netcoreapp3.1
if exist %zipName% ( del /S /Q  %zipName% )
cd %programFolder%
REM a 为压缩命令，-tzip：指定格式 -r:递归查找 zipName:zip文件名， .\表示当前文件夹下内容， -x! 后跟需要排除的文件。
7z a -tzip -r %zipName% .\ -x!.\*.txt -x!.\backup_list_in_database.json -x!.\*.txt -x!.\config.json -x!.\database_item.json -x!.\*.txt
move %zipName% %currentPath%..\..\..\
pause