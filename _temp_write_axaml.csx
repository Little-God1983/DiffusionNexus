// Helper script to create the AXAML file
var path = @"E:\AI\DiffusionNexus\DiffusionNexus.UI\Views\Dialogs\DownloadLoraVersionDialog.axaml";
var content = """
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:DiffusionNexus.UI.ViewModels"
        xmlns:converters="using:DiffusionNexus.UI.Converters"
        x:Class="DiffusionNexus.UI.Views.Dialogs.DownloadLoraVersionDialog"
        x:DataType="vm:DownloadLoraVersionDialogViewModel"
        Width="560"
        SizeToContent="Height"
        WindowStartupLocation="CenterOwner"
        CanResize="False"
        Background="#252526">

  <Grid Margin="24" RowDefinitions="Auto,*,Auto">

    <!-- Title -->
    <StackPanel Grid.Row="0" Margin="0,0,0,20">
      <TextBlock Text="Download LoRA Version"
                 FontSize="18"
                 FontWeight="SemiBold"
                 Foreground="#FFCC00"/>
    </StackPanel>

    <!-- Main Content -->
    <StackPanel Grid.Row="1" Spacing="16" Margin="0,0,0,24">

      <!-- Model Info Summary -->
      <Border Background="#1A1A1A"
              CornerRadius="8"
              Padding="16">
        <Grid ColumnDefinitions="Auto,*"
              RowDefinitions="Auto,Auto,Auto,Auto"
              Margin="0">

          <TextBlock Grid.Row="0" Grid.Column="0"
                     Text="Model:"
                     Foreground="#FF66AA"
                     FontWeight="SemiBold"
                     Margin="0,0,12,6"/>
          <TextBlock Grid.Row="0" Grid.Column="1"
                     Text="{Binding ModelName}"
                     Foreground="White"
                     TextWrapping="Wrap"
                     Margin="0,0,0,6"/>

          <TextBlock Grid.Row="1" Grid.Column="0"
                     Text="Version:"
                     Foreground="#FF66AA"
                     FontWeight="SemiBold"
                     Margin="0,0,12,6"/>
          <TextBlock Grid.Row="1" Grid.Column="1"
                     Text="{Binding VersionName}"
                     Foreground="White"
                     Margin="0,0,0,6"/>

          <TextBlock Grid.Row="2" Grid.Column="0"
                     Text="File:"
                     Foreground="#FF66AA"
                     FontWeight="SemiBold"
                     Margin="0,0,12,6"/>
          <TextBlock Grid.Row="2" Grid.Column="1"
                     Text="{Binding FileName}"
                     Foreground="White"
                     TextWrapping="Wrap"
                     Margin="0,0,0,6"/>

          <TextBlock Grid.Row="3" Grid.Column="0"
                     Text="Size:"
                     Foreground="#FF66AA"
                     FontWeight="SemiBold"
                     Margin="0,0,12,0"/>
          <StackPanel Grid.Row="3" Grid.Column="1"
                      Orientation="Horizontal"
                      Spacing="12">
            <TextBlock Text="{Binding FileSizeDisplay}"
                       Foreground="White"/>
            <TextBlock Text="{Binding BaseModel}"
                       Foreground="#888"
                       FontSize="12"
                       VerticalAlignment="Center"/>
          </StackPanel>
        </Grid>
      </Border>

      <!-- Destination Options -->
      <StackPanel Spacing="8">
        <TextBlock Text="Download destination"
                   FontWeight="SemiBold"
                   Margin="0,0,0,4"/>

        <!-- Download to existing source folder -->
        <Border Background="#1A1A1A"
                CornerRadius="8"
                Padding="16,12"
                BorderThickness="2"
                BorderBrush="{Binding IsDownloadToExisting, Converter={x:Static converters:BoolConverters.ToAccentBorder}}">
          <Grid ColumnDefinitions="Auto,*">
            <RadioButton Grid.Column="0"
                         GroupName="DownloadOption"
                         IsChecked="{Binding IsDownloadToExisting, Mode=TwoWay}"
                         VerticalAlignment="Top"
                         Margin="0,2,12,0"/>
            <StackPanel Grid.Column="1" Spacing="8">
              <TextBlock Text="Download to existing source folder"
                         FontWeight="SemiBold"
                         FontSize="14"/>
              <TextBlock Text="Save to one of your configured LoRA source folders"
                         Opacity="0.6"
                         FontSize="12"/>
              <ComboBox ItemsSource="{Binding SourceFolders}"
                        SelectedItem="{Binding SelectedSourceFolder}"
                        IsEnabled="{Binding IsDownloadToExisting}"
                        HorizontalAlignment="Stretch"
                        Margin="0,4,0,0"/>
            </StackPanel>
          </Grid>
        </Border>

        <!-- Download to custom folder -->
        <Border Background="#1A1A1A"
                CornerRadius="8"
                Padding="16,12"
                BorderThickness="2"
                BorderBrush="{Binding IsDownloadToFolder, Converter={x:Static converters:BoolConverters.ToAccentBorder}}">
          <Grid ColumnDefinitions="Auto,*">
            <RadioButton Grid.Column="0"
                         GroupName="DownloadOption"
                         IsChecked="{Binding IsDownloadToFolder, Mode=TwoWay}"
                         VerticalAlignment="Top"
                         Margin="0,2,12,0"/>
            <StackPanel Grid.Column="1" Spacing="8">
              <TextBlock Text="Download to folder"
                         FontWeight="SemiBold"
                         FontSize="14"/>
              <TextBlock Text="Choose any folder on your system"
                         Opacity="0.6"
                         FontSize="12"/>
              <Grid ColumnDefinitions="*,Auto" Margin="0,4,0,0">
                <TextBox Grid.Column="0"
                         Text="{Binding CustomFolderPath, Mode=TwoWay}"
                         Watermark="Select a folder..."
                         IsEnabled="{Binding IsDownloadToFolder}"
                         IsReadOnly="True"/>
                <Button Grid.Column="1"
                        Content="Browse..."
                        Command="{Binding BrowseFolderCommand}"
                        IsEnabled="{Binding IsDownloadToFolder}"
                        Margin="8,0,0,0"
                        Padding="12,6"/>
              </Grid>
            </StackPanel>
          </Grid>
        </Border>
      </StackPanel>

      <!-- No source folders warning -->
      <Border Background="#7D5D2D"
              CornerRadius="8"
              Padding="12"
              IsVisible="{Binding !SourceFolders.Count}">
        <StackPanel Orientation="Horizontal" Spacing="8">
          <TextBlock Text="!" Foreground="#FFCC00" FontSize="16" FontWeight="Bold"/>
          <TextBlock Text="No LoRA source folders configured. Add source folders in Settings or use the folder option."
                     Opacity="0.9"
                     FontSize="12"
                     TextWrapping="Wrap"
                     VerticalAlignment="Center"/>
        </StackPanel>
      </Border>
    </StackPanel>

    <!-- Buttons -->
    <StackPanel Grid.Row="2"
                Orientation="Horizontal"
                HorizontalAlignment="Right"
                Spacing="8">
      <Button Content="Cancel"
              Width="100"
              Click="OnCancelClick"/>
      <Button Content="Download"
              MinWidth="120"
              Background="#FFCC00"
              Foreground="#1E1E1E"
              FontWeight="SemiBold"
              IsDefault="True"
              IsEnabled="{Binding CanDownload}"
              Click="OnDownloadClick"/>
    </StackPanel>

  </Grid>
</Window>
""";
System.IO.File.WriteAllText(path, content);
Console.WriteLine("Done");
