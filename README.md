# small_window2

���ܱ��룺
���ȣ�profile:

dotnet publish -c Release -r win-x64 ^
  -p:PublishReadyToRun=true ^
  -p:ReadyToRunProfileGuidedOptimization=Generate

���:
bin\Release\net8.0-windows\win-x64\publish\MyApp.mibc