# Format çarkı
Gezgin'de bir görselin üstüne gelip Ctrl'yi ~0,4 sn basılı tutun → çark açılır.
Fareyle format seçin, Ctrl'yi bırakın → dosya yanına dönüştürülür. Esc / başka tuş = iptal.

Derleme (.NET 8 SDK gerekir):
  dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
Çıktı: bin\Release\net8.0-windows\win-x64\publish\FormatCarki.exe
