using RepoOPS.Host;

var app = RepoOpsWebApp.Create(args);

app.Urls.Clear();
app.Urls.Add("http://localhost:5088");

app.Run();
