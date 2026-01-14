# Fix Instructions for MainWindow.axaml

## Issue 1: Cover Images Not Showing

In the "My Games" panel ListBox ItemTemplate, the game item needs to display the cover image.

**Change:** Add cover image display with fallback icon

```xaml
<!-- In Panel 1: My Games, replace the ListBox.ItemTemplate DataTemplate content with: -->
<Border Classes="game-item">
    <Grid ColumnDefinitions="Auto,*">
        <!-- Cover Image -->
        <Border Grid.Column="0" 
                Width="60" Height="90" 
                Margin="0,0,8,0"
                CornerRadius="4"
                ClipToBounds="True"
                Background="#2D2D30">
            <Image Source="{Binding CoverImage}" 
                   Stretch="UniformToFill"
                   IsVisible="{Binding CoverImage, Converter={StaticResource NullToBool}}"/>
            <TextBlock Text="??" 
                       FontSize="24"
                       HorizontalAlignment="Center"
                       VerticalAlignment="Center"
                       Foreground="#6B7280"
                       IsVisible="{Binding CoverImage, Converter={StaticResource NullToBool}, ConverterParameter=Invert}"/>
        </Border>
        
        <!-- Game Info -->
        <StackPanel Grid.Column="1" Spacing="4" VerticalAlignment="Center">
            <StackPanel Orientation="Horizontal" Spacing="8">
                <TextBlock Text="{Binding Name}" FontWeight="SemiBold" FontSize="12" 
                           Foreground="White" TextTrimming="CharacterEllipsis" MaxWidth="180"/>
                <Border Background="{DynamicResource ErrorBrush}" 
                        CornerRadius="2" Padding="4,1"
                        IsVisible="{Binding IsHidden}">
                    <TextBlock Text="HIDDEN" FontSize="8" FontWeight="Bold" Foreground="White"/>
                </Border>
            </StackPanel>
            <StackPanel Orientation="Horizontal" Spacing="8">
                <TextBlock Text="{Binding BuildId}" FontSize="10" 
                           Foreground="{DynamicResource AccentBrush}"/>
                <TextBlock Text="-" FontSize="10" Foreground="#6B7280"/>
                <TextBlock Text="{Binding FormattedSize}" FontSize="10" Foreground="#6B7280"/>
            </StackPanel>
        </StackPanel>
    </Grid>
</Border>
```

## Issue 2: Settings Button Does Nothing

In the Status Bar section, the Settings button needs a command.

**Change:** Add Command binding

```xaml
<!-- Find the Settings button and change from: -->
<Button Background="#3C3C3C" Padding="12,4"
        ToolTip.Tip="Open Settings">
    <TextBlock Text="? Settings" FontSize="10" Foreground="#9CA3AF"/>
</Button>

<!-- To: -->
<Button Background="#3C3C3C" Padding="12,4"
        Command="{Binding OpenSettingsCommand}"
        ToolTip.Tip="Open Settings">
    <TextBlock Text="? Settings" FontSize="10" Foreground="#9CA3AF"/>
</Button>
```

## Issue 3: Log Panel Doesn't Close When Clicking Outside

**Change 1:** Add x:Name to Window and Grid, and add PointerPressed event

```xaml
<!-- Change the Window opening tag from: -->
<Window xmlns="https://github.com/avaloniaui"
        ...
        Background="#1E1E1E">

<!-- To: -->
<Window xmlns="https://github.com/avaloniaui"
        ...
        Background="#1E1E1E"
        x:Name="MainWindowRoot">

<!-- Change the main Grid from: -->
<Grid RowDefinitions="Auto,Auto,*,Auto">

<!-- To: -->
<Grid RowDefinitions="Auto,Auto,*,Auto" PointerPressed="MainGrid_PointerPressed">
```

**Change 2:** Add x:Name to LogBorder

```xaml
<!-- Find the Log Panel Border and add x:Name: -->
<Border x:Name="LogBorder"
        Grid.Row="2" 
        Background="#252526" 
        ...>
```

**Change 3:** Add PointerPressed handler in MainWindow.axaml.cs (Already done)

---

## Files Modified:
1. `Views/MainWindow.axaml` - UI fixes
2. `Views/MainWindow.axaml.cs` - Event handler for log closing
3. `ViewModels/MainViewModel.cs` - Added OpenSettingsCommand
4. `Converters/AvaloniaConverters.cs` - Updated NullToBoolConverter to support inversion

## Testing:
1. Run the app and scan games - cover images should load and display
2. Click Settings button - should show settings dialog
3. Click Log button to open, then click anywhere outside log panel - should close
