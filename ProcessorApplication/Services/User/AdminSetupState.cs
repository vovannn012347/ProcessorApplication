namespace ProcessorApplication.Services.User;

//singleton setup class
public class AdminSetupState
{
    public bool IsAdminChecked { get; private set; } = false;
    public bool IsAdminConfigured { get; private set; } = false;

    public void SetAdminChecked()
    {
        IsAdminChecked = true;
    }
    public void SetAdminConfigured()
    {
        IsAdminConfigured = true;
    }
}