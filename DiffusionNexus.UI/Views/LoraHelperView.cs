using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Layout;
using System.Collections.Generic;



namespace DiffusionNexus.UI.Views

{

    public class LoraHelperView : UserControl

    {

        // UI Control references

        private TextBox searchBox;

        private TreeView folderTreeView;

        private WrapPanel cardsPanel;



        public LoraHelperView()

        {

            this.BuildUserInterface();

        }



        private void BuildUserInterface()

        {

            // Main grid with header and content

            var mainGrid = new Grid

            {

                RowDefinitions = new RowDefinitions("Auto,*")

            };



            // Header navigation bar

            var headerBorder = new Border

            {

                Background = new SolidColorBrush(Color.Parse("#2D2D30")),

                Padding = new Thickness(10, 5),

                Height = 50

            };

            Grid.SetRow(headerBorder, 0);



            var headerGrid = new Grid

            {

                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")

            };



            // Search bar

            var searchPanel = new StackPanel

            {

                Orientation = Orientation.Horizontal,

                VerticalAlignment = VerticalAlignment.Center

            };

            Grid.SetColumn(searchPanel, 0);



            searchBox = new TextBox

            {

                Width = 250,

                Watermark = "Search loras...",

                Margin = new Thickness(0, 0, 10, 0)

            };

            searchPanel.Children.Add(searchBox);



            // Center title

            var titlePanel = new StackPanel

            {

                Orientation = Orientation.Horizontal,

                HorizontalAlignment = HorizontalAlignment.Center,

                VerticalAlignment = VerticalAlignment.Center

            };

            Grid.SetColumn(titlePanel, 1);



            var titleText = new TextBlock

            {

                Text = "Lora Helper",

                FontSize = 18,

                FontWeight = FontWeight.Bold,

                Foreground = Brushes.White

            };

            titlePanel.Children.Add(titleText);



            // Right buttons

            var buttonsPanel = new StackPanel

            {

                Orientation = Orientation.Horizontal,

                HorizontalAlignment = HorizontalAlignment.Right,

                VerticalAlignment = VerticalAlignment.Center,

                Spacing = 10

            };

            Grid.SetColumn(buttonsPanel, 2);



            var refreshButton = new Button

            {

                Content = "Refresh",

                Width = 80

            };

            buttonsPanel.Children.Add(refreshButton);



            var settingsButton = new Button

            {

                Content = "Settings",

                Width = 80

            };

            buttonsPanel.Children.Add(settingsButton);



            // Add all header elements

            headerGrid.Children.Add(searchPanel);

            headerGrid.Children.Add(titlePanel);

            headerGrid.Children.Add(buttonsPanel);

            headerBorder.Child = headerGrid;



            // Main content area

            var contentGrid = new Grid

            {

                ColumnDefinitions = new ColumnDefinitions("300,*")

            };

            Grid.SetRow(contentGrid, 1);



            // Folder structure on left

            var folderBorder = new Border

            {

                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),

                BorderBrush = new SolidColorBrush(Color.Parse("#333333")),

                BorderThickness = new Thickness(0, 0, 1, 0)

            };

            Grid.SetColumn(folderBorder, 0);



            var folderGrid = new Grid

            {

                RowDefinitions = new RowDefinitions("Auto,*")

            };



            var folderHeaderText = new TextBlock

            {

                Text = "Folder Structure",

                Margin = new Thickness(10, 10, 0, 5),

                FontWeight = FontWeight.Bold,

                Foreground = Brushes.White

            };

            Grid.SetRow(folderHeaderText, 0);



            folderTreeView = new TreeView

            {

                Background = Brushes.Transparent,

                Margin = new Thickness(5)

            };

            Grid.SetRow(folderTreeView, 1);



            // Add sample folders

            var modelsItem = new TreeViewItem { Header = "Models", IsExpanded = true };

            modelsItem.Items.Add(new TreeViewItem { Header = "Lora" });

            modelsItem.Items.Add(new TreeViewItem { Header = "Stable Diffusion" });

            modelsItem.Items.Add(new TreeViewItem { Header = "Embeddings" });



            var imagesItem = new TreeViewItem { Header = "Generated Images" };

            imagesItem.Items.Add(new TreeViewItem { Header = "Portraits" });

            imagesItem.Items.Add(new TreeViewItem { Header = "Landscapes" });



            folderTreeView.Items.Add(modelsItem);

            folderTreeView.Items.Add(imagesItem);



            folderGrid.Children.Add(folderHeaderText);

            folderGrid.Children.Add(folderTreeView);

            folderBorder.Child = folderGrid;



            // Card layout on right

            var cardBorder = new Border

            {

                Background = new SolidColorBrush(Color.Parse("#2A2A2A"))

            };

            Grid.SetColumn(cardBorder, 1);



            var cardGrid = new Grid

            {

                RowDefinitions = new RowDefinitions("Auto,*")

            };



            var cardHeaderPanel = new StackPanel

            {

                Orientation = Orientation.Horizontal,

                Margin = new Thickness(10, 10, 10, 5),

                Spacing = 10

            };

            Grid.SetRow(cardHeaderPanel, 0);



            var cardHeaderText = new TextBlock

            {

                Text = "Card View",

                FontWeight = FontWeight.Bold,

                VerticalAlignment = VerticalAlignment.Center,

                Foreground = Brushes.White

            };

            cardHeaderPanel.Children.Add(cardHeaderText);



            var viewComboBox = new ComboBox

            {

                Width = 150,

                SelectedIndex = 0

            };

            viewComboBox.Items.Add(new ComboBoxItem { Content = "Grid View" });

            viewComboBox.Items.Add(new ComboBoxItem { Content = "List View" });

            cardHeaderPanel.Children.Add(viewComboBox);



            var cardScrollViewer = new ScrollViewer

            {

                Margin = new Thickness(10)

            };

            Grid.SetRow(cardScrollViewer, 1);



            // Using WrapPanel for a grid-like layout

            cardsPanel = new WrapPanel

            {

                Orientation = Orientation.Horizontal

            };



            // Create sample cards

            for (int i = 1; i <= 10; i++)

            {

                var loraCardBorder = new Border

                {

                    Background = new SolidColorBrush(Color.Parse("#333333")),

                    CornerRadius = new CornerRadius(4),

                    Padding = new Thickness(10),

                    Margin = new Thickness(5),

                    Width = 200,

                    Height = 200

                };



                var cardStackPanel = new StackPanel { Spacing = 5 };



                var imagePanel = new Panel

                {

                    Height = 120,

                    Background = new SolidColorBrush(Color.Parse("#222222"))

                };



                var nameText = new TextBlock

                {

                    Text = $"Sample Lora {i}",

                    FontWeight = FontWeight.Bold,

                    Foreground = Brushes.White

                };



                var descText = new TextBlock

                {

                    Text = "This is a sample lora card for demonstration purposes",

                    Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),

                    TextWrapping = TextWrapping.Wrap

                };



                cardStackPanel.Children.Add(imagePanel);

                cardStackPanel.Children.Add(nameText);

                cardStackPanel.Children.Add(descText);

                loraCardBorder.Child = cardStackPanel;



                cardsPanel.Children.Add(loraCardBorder);

            }



            cardScrollViewer.Content = cardsPanel;

            cardGrid.Children.Add(cardHeaderPanel);

            cardGrid.Children.Add(cardScrollViewer);

            cardBorder.Child = cardGrid;



            // Add main content sections

            contentGrid.Children.Add(folderBorder);

            contentGrid.Children.Add(cardBorder);



            // Combine everything

            mainGrid.Children.Add(headerBorder);

            mainGrid.Children.Add(contentGrid);



            this.Content = mainGrid;

        }

    }

}