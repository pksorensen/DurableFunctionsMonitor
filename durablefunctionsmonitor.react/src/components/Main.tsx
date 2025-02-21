import * as React from 'react';
import { observer } from 'mobx-react';

import { AppBar, Breadcrumbs, Box, Link, TextField, Toolbar, Typography } from '@material-ui/core';
import { MuiPickersUtilsProvider } from '@material-ui/pickers';
import Autocomplete from '@material-ui/lab/Autocomplete';
import MomentUtils from '@date-io/moment';

import './Main.css';

import { LoginIcon } from './LoginIcon';
import { MainMenu } from './MainMenu';
import { MainState } from '../states/MainState';
import { Orchestrations } from './Orchestrations';
import { OrchestrationDetails } from './OrchestrationDetails';
import { PurgeHistoryDialog } from './PurgeHistoryDialog';
import { CleanEntityStorageDialog } from './CleanEntityStorageDialog';

const logo = require('../logo.svg');

// The main application view
@observer
export class Main extends React.Component<{ state: MainState }> {

    render(): JSX.Element {
        const state = this.props.state;

        return (
            <MuiPickersUtilsProvider utils={MomentUtils}><>

                {!state.loginState && (
                    <Box height={20}/>
                )}
                
                {!!state.loginState && (
                    <AppBar position="static" color="default" className="app-bar">
                        <Toolbar>

                            {state.loginState.isLoggedIn && !!state.mainMenuState && (
                                <MainMenu state={state.mainMenuState} />
                            )}

                            <img src={logo} width="30px"></img>
                            <Box width={5} />

                            <Typography variant="h6" color="inherit" className="title-typography">
                                Durable Functions Monitor
                            </Typography>

                            <Breadcrumbs color="inherit">
                                <Link color="inherit" href="/">
                                    / instances
                                </Link>

                                {!state.orchestrationDetailsState ?
                                    (
                                        <Autocomplete
                                            className="instance-id-input"
                                            freeSolo
                                            options={state.isExactMatch ? [] : state.suggestions}
                                            value={state.typedInstanceId}
                                            onChange={(evt, newValue) => {
                                                state.typedInstanceId = newValue ?? '';
                                                if (!!newValue) {
                                                    state.goto();
                                                }
                                            }}
                                            renderInput={(params) => (
                                                <TextField
                                                    {...params}
                                                    className={state.isExactMatch ? 'instance-id-valid' : null}
                                                    size="small"
                                                    label="instanceId to go to..."
                                                    variant="outlined"
                                                    onChange={(evt) => state.typedInstanceId = evt.target.value as string}
                                                    onKeyPress={(evt) => this.handleKeyPress(evt)}
                                                />
                                            )}
                                        />
                                    )
                                    :
                                    (<Typography color="inherit">
                                        {state.orchestrationDetailsState.orchestrationId}
                                    </Typography>)
                                }

                            </Breadcrumbs>

                            <Typography style={{ flex: 1 }} />

                            <LoginIcon state={state.loginState} />
                        </Toolbar>
                    </AppBar>
                )}

                {!!state.orchestrationsState && (!state.loginState || state.loginState.isLoggedIn) && (
                    <Orchestrations state={state.orchestrationsState} />
                )}

                {!!state.orchestrationDetailsState && (!state.loginState || state.loginState.isLoggedIn) && (
                    <OrchestrationDetails state={state.orchestrationDetailsState} />
                )}

                <PurgeHistoryDialog state={state.purgeHistoryDialogState}/>
                <CleanEntityStorageDialog state={state.cleanEntityStorageDialogState} />

            </></MuiPickersUtilsProvider>
        );
    }

    private handleKeyPress(event: React.KeyboardEvent<HTMLDivElement>) {
        if (event.key === 'Enter') {
            // Otherwise the event will bubble up and the form will be submitted
            event.preventDefault();

            this.props.state.goto();
        }
    }
}