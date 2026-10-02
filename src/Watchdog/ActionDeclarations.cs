using TheKrystalShip.KGSM.ComponentConfig;

// What a person may do with this daemon, through the node's API: the API checks it before it relays to
// the socket, so it is declared here, where it is performed, and checked there.
[assembly: Action("watchdog:supervision.read", "See how the supervisor is running this machine's servers",
    DeclaredEffect.Read, DeclaredScope.Node)]
