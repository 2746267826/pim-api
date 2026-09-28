package com.pim.app.ui.root

import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import com.pim.app.ui.location.LocationScreen
import com.pim.app.ui.schedule.SchedulePolicyScreen
import com.pim.app.ui.settings.SettingsScreen
import com.pim.app.ui.status.StatusCenterScreen
import com.pim.app.ui.theme.PimTheme
import com.pim.app.ui.today.TodayScreen
import com.pim.app.ui.tracks.TracksScreen

@Composable
fun PimRootScreen(initialDestination: PimDestination = PimDestination.Today) {
    var selected by rememberSaveable(initialDestination.name) { mutableStateOf(initialDestination) }
    var savedTracksUrl by rememberSaveable { mutableStateOf<String?>(null) }
    // WO-ANDROID-POLICY-TRANSITION-20260928 REQ-3（D-10）：状态页写入失败告警的动作按钮
    // 要把用户带到设置页「策略切换历史」板块（不新建二级页面）。
    var scrollToPolicyHistory by rememberSaveable { mutableStateOf(false) }

    PimTheme {
        Scaffold(
            bottomBar = {
                PimBottomNavigation(
                    selected = selected,
                    onSelected = { selected = it }
                )
            }
        ) { innerPadding ->
            val modifier = Modifier.padding(innerPadding)
            when (selected) {
                PimDestination.Today -> TodayScreen(modifier, onOpenSettings = { selected = PimDestination.Settings })
                PimDestination.Location -> LocationScreen(
                    modifier = modifier,
                    onOpenSettings = { selected = PimDestination.Settings }
                )
                PimDestination.Tracks -> TracksScreen(
                    modifier = modifier,
                    onOpenSettings = { selected = PimDestination.Settings },
                    savedUrl = savedTracksUrl,
                    onUrlChanged = { savedTracksUrl = it }
                )
                PimDestination.Schedule -> SchedulePolicyScreen(
                    modifier = modifier,
                    onOpenSettings = { selected = PimDestination.Settings }
                )
                PimDestination.Status -> StatusCenterScreen(
                    modifier = modifier,
                    onOpenSettings = { selected = PimDestination.Settings },
                    onOpenPolicyTransitionHistory = {
                        scrollToPolicyHistory = true
                        selected = PimDestination.Settings
                    }
                )
                PimDestination.Settings -> SettingsScreen(
                    modifier = modifier,
                    scrollToPolicyTransitionHistory = scrollToPolicyHistory,
                    onPolicyTransitionHistoryScrolled = { scrollToPolicyHistory = false }
                )
            }
        }
    }
}

@Composable
internal fun PimBottomNavigation(
    selected: PimDestination,
    onSelected: (PimDestination) -> Unit
) {
    NavigationBar(modifier = Modifier.testTag("pim-nav-bar")) {
        PimDestination.entries.forEach { destination ->
            NavigationBarItem(
                selected = selected == destination,
                onClick = { onSelected(destination) },
                icon = { Icon(destination.icon, contentDescription = destination.label) },
                label = { Text(destination.label) },
                modifier = Modifier.weight(1f).testTag("pim-nav-${destination.name.lowercase()}")
            )
        }
    }
}
