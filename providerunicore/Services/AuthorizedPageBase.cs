// Components/AuthorizedPageBase.cs
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace unicoreprovider.Services;

public class AuthorizedPageBase : ComponentBase
{
    [Inject] protected IAuthStateService AuthState { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && string.IsNullOrEmpty(AuthState.FirebaseUid))
        {
            var authData = await JS.InvokeAsync<AuthData>("authStorage.load");
            if (!string.IsNullOrEmpty(authData?.uid))
            {
                AuthState.SetAuthState(authData.uid, authData.email ?? "", authData.name ?? "");
                StateHasChanged();
            }
            else
            {
                Nav.NavigateTo("/", forceLoad: false);
            }
        }
    }

    // Lowercase property names to match the JS object from authStorage.load()
    private class AuthData
    {
        public string? uid { get; set; }
        public string? email { get; set; }
        public string? name { get; set; }
    }
}
