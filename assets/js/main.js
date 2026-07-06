    consumeExternalTokenAccountFromUrl();
    render();
    if (state.loggedIn) refreshBridgeData({ silent: true });
