
 = 'C:\Users\isu\Downloads\nota-main\nota-main\src\managed\Knox.App\bin\Release\net10.0\Avalonia.Controls.dll'
 = [System.Reflection.Assembly]::LoadFile()
 = .GetType('Avalonia.Controls.Window')
.GetProperties() | ForEach-Object {
    if (.Name -like '*Extend*' -or .Name -like '*Chrome*' -or .Name -like '*Title*') {
        Write-Host (.Name + ' -> ' + .PropertyType.FullName)
    }
}
