// Components/AuthorizedPageBase.cs
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace unicoreprovider.Services;

public class AuthorizedPageBase : ComponentBase
{
    [Inject] protected IAuthStateService AuthState { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    private bool _redirecting;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_redirecting) return;

        if (string.IsNullOrEmpty(AuthState.FirebaseUid))
        {
            var authData = await JS.InvokeAsync<AuthData>("authStorage.load");

            if (!string.IsNullOrEmpty(authData?.uid))
            {
                AuthState.SetAuthState(authData.uid, authData.email ?? "", authData.name ?? "");
                // No need to StateHasChanged(); setting state should cause it anyway, but it's okay if you keep it.
            }
            else
            {
                _redirecting = true;
                Nav.NavigateTo("/", replace: true);
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
