# small_window2

性能编译：
首先，profile:

dotnet publish -c Release -r win-x64 ^
  -p:PublishReadyToRun=true ^
  -p:ReadyToRunProfileGuidedOptimization=Generate

获得:
bin\Release\net8.0-windows\win-x64\publish\MyApp.mibc