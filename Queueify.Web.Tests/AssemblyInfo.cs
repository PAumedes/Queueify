// Several tests start the whole web app, and apps that share one account database migrate it at the same
// time and trip over each other under load. One collection for the whole assembly runs them one at a time:
// slower but steady.
[assembly: Xunit.CollectionBehavior(Xunit.CollectionBehavior.CollectionPerAssembly)]
